using System.Diagnostics;
using Ago.Chat.Application.Abstractions;
using Ago.Chat.Domain;
using Ago.Chat.Infrastructure.Postgres;
using Ago.Chat.Infrastructure.Postgres.Pipeline;
using Ago.Platform.Hosting;
using Ago.Platform.Kernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Ago.Chat.Integration.Tests;

/// <summary>
/// `23-73`: <see cref="MessageBatchWriter"/>'s own watchdog-reset hook, real Postgres - the operator-
/// outbound-message half of the two reset hooks this item builds
/// (<see cref="Ago.Chat.Api.Auth.OperatorIdentityClaimsTransformation"/>'s own login half is proven
/// separately, in <c>ActiveSiteResolutionTests.ARealAuthenticatedRequest_TouchesTheInactivityWatchdog</c>,
/// against a real authenticated request rather than the pipeline directly). Every assertion here reads
/// the shadow-property columns back through <c>EF.Property</c>, the same "shadow property, read via
/// <c>EF.Property</c>, never a mapped C# property" shape <c>ClaimConversationEndpointTests</c>'s own
/// <c>active_chats</c> assertions already establish for a different column on a different table.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class SiteActivityWatchdogTests(PostgresFixture fixture)
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task FlushAsync_OperatorMessage_TouchesTheSiteWatchdog()
    {
        var (siteId, _, operatorId, conversationId) = await SeedAssignedConversationAsync();

        var result = await FlushOneAsync(conversationId, MessageAuthorKind.Operator, operatorId.Value, "hi there");
        Assert.True(result.IsSuccess);

        var watchdog = await ReadWatchdogAsync(siteId);
        Assert.NotNull(watchdog);
        Assert.True(watchdog.Value > DateTimeOffset.UtcNow - TimeSpan.FromMinutes(1));
    }

    /// <summary>
    /// `23-73`'s own explicit, author-stated rule: "входящий трафик... получается повисает в пустоте" -
    /// visitor/inbound traffic never resets the counter. A visitor message on a freshly created
    /// conversation (nobody has ever assigned an operator to it - <c>Waiting</c>, per
    /// <see cref="Conversation.Start"/>) must leave the site's watchdog exactly as untouched as it was
    /// the moment the site was created: still <see langword="null"/>, not merely "unchanged from some
    /// earlier value" - a weaker assertion could pass even if visitor traffic wrongly set it once.
    /// </summary>
    [Fact]
    public async Task FlushAsync_VisitorMessage_NeverTouchesTheSiteWatchdog()
    {
        var (siteId, visitorId, conversationId) = await SeedWaitingConversationAsync();

        var result = await FlushOneAsync(conversationId, MessageAuthorKind.Visitor, visitorId, "hello, is anyone there?");
        Assert.True(result.IsSuccess);

        var watchdog = await ReadWatchdogAsync(siteId);
        Assert.Null(watchdog);
    }

    private MessageBatchWriter CreateWriter() =>
        new(fixture.DataSource, new SystemClock(), new UuidV7Generator(), new NoOpCache(),
            Options.Create(new SiteActivityWatchdogOptions()), NullLogger<MessageBatchWriter>.Instance);

    private async Task<Result<int>> FlushOneAsync(
        ConversationId conversationId, MessageAuthorKind authorKind, Guid authorId, string body)
    {
        var writer = CreateWriter();
        var ack = new TaskCompletionSource<Result<int>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var item = new InboundMessage(
            new PendingMessage(conversationId, authorKind, authorId, new MessageBody(body), null),
            ack, Stopwatch.GetTimestamp());
        await writer.FlushAsync([item], CancellationToken.None);
        return await ack.Task;
    }

    private async Task<DateTimeOffset?> ReadWatchdogAsync(SiteId siteId)
    {
        await using var db = fixture.CreateDbContext();
        return await db.Sites
            .Where(s => s.Id == siteId)
            .Select(s => EF.Property<DateTimeOffset?>(s, "LastOperatorActivityAt"))
            .SingleAsync();
    }

    private async Task<(SiteId SiteId, Guid VisitorId, ConversationId ConversationId)> SeedWaitingConversationAsync()
    {
        var siteId = new SiteId(Guid.NewGuid());
        var visitorId = new VisitorId(Guid.NewGuid());
        var conversationId = new ConversationId(Guid.NewGuid());

        await using var db = fixture.CreateDbContext();
        db.Sites.Add(new Site(siteId, $"site_{siteId.Value:N}", []));
        db.Visitors.Add(new Visitor(visitorId, siteId, Now));
        db.Conversations.Add(Conversation.Start(conversationId, siteId, visitorId, Now));
        await db.SaveChangesAsync();

        return (siteId, visitorId.Value, conversationId);
    }

    private async Task<(SiteId SiteId, Guid VisitorId, OperatorId OperatorId, ConversationId ConversationId)> SeedAssignedConversationAsync()
    {
        var siteId = new SiteId(Guid.NewGuid());
        var visitorId = new VisitorId(Guid.NewGuid());
        var operatorId = new OperatorId(Guid.NewGuid());
        var conversationId = new ConversationId(Guid.NewGuid());

        await using var db = fixture.CreateDbContext();
        db.Sites.Add(new Site(siteId, $"site_{siteId.Value:N}", []));
        db.Visitors.Add(new Visitor(visitorId, siteId, Now));
        db.Operators.Add(new Operator(operatorId, siteId, OperatorStatus.Online, capacity: 5));
        var conversation = Conversation.Start(conversationId, siteId, visitorId, Now);
        conversation.AssignTo(operatorId, Now);
        db.Conversations.Add(conversation);
        await db.SaveChangesAsync();

        return (siteId, visitorId.Value, operatorId, conversationId);
    }
}
