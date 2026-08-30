using Ago.Chat.Application.Abstractions;
using Ago.Chat.Application.UseCases.AutoCloseConversation;
using Ago.Chat.Application.UseCases.ReceiveChannelMessage;
using Ago.Chat.Application.UseCases.SendMessage;
using Ago.Chat.Application.UseCases.StartConversation;
using Ago.Chat.Contracts;
using Ago.Chat.Domain;
using Ago.Chat.Infrastructure.Postgres;
using Ago.Chat.Infrastructure.Postgres.Persistence;
using Ago.Chat.Worker;
using Ago.Platform.Hosting;
using Ago.Platform.Kernel;
using Ago.Platform.Persistence.Postgres;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Ago.Chat.Integration.Tests;

/// <summary>
/// `18-06`: real Postgres, the real domain path (`AutoCloseConversationHandler` ->
/// <c>Conversation.Close()</c> -> the outbox -> `6-09`'s capacity release), and the real reuse-or-new
/// logic `StartConversationHandler`/`ConversationRepository.GetActiveForVisitorAsync` already own -
/// the same bar `CloseConversationOutboxTests` and `OperatorConversationReleaserTests` already set for
/// the handlers this job builds on. This is a state change only: nothing here deletes or archives a
/// row - every assertion below reads the closed conversation's own row back, unchanged except for
/// `state`.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class AutoCloseInactiveConversationsJobTests(PostgresFixture fixture)
{
    // Real time, not a fixed date - MessageUniqueSequenceTests' own remarks explain why: the
    // partitioned messages table only ever has partitions for the current month and the next two.
    // Truncated to whole seconds so it round-trips through timestamptz unchanged.
    private static readonly DateTimeOffset Now =
        new(DateTimeOffset.UtcNow.Ticks / TimeSpan.TicksPerSecond * TimeSpan.TicksPerSecond, TimeSpan.Zero);

    [Fact]
    public async Task RunOnceAsync_ClosesAnAssignedWidgetConversationPastItsWindow_ThroughTheRealDomainPath()
    {
        var widgetWindow = TimeSpan.FromHours(1);
        var seeded = await SeedAssignedConversationAsync(
            createdAt: Now - widgetWindow - TimeSpan.FromMinutes(1), holdsCapacityClaim: true);

        await CreateJob(new AutoCloseInactiveConversationsJobOptions
        {
            WidgetInactivityWindow = widgetWindow,
            // Kept far out of the way so this test proves only the widget path.
            DefaultChannelInactivityWindow = TimeSpan.FromDays(365),
        }).RunOnceAsync(CancellationToken.None);

        await using var verify = fixture.CreateDbContext();
        var conversation = await verify.Conversations.SingleAsync(c => c.Id == seeded.ConversationId);
        Assert.Equal(ConversationState.Closed, conversation.State);

        // The outbox row is ConversationClosedMapper's own ConversationEnded, the exact envelope
        // CloseConversationOutboxTests already proves for an operator's own close - same shape, system
        // trigger.
        var outboxRow = await verify.Set<OutboxMessage>().SingleAsync(o => o.Id == seeded.ConversationId.Value);
        Assert.Equal(nameof(ConversationEnded), outboxRow.Type);
        Assert.Equal(seeded.ConversationId.Value.ToString(), outboxRow.PartitionKey);
        Assert.Null(outboxRow.PublishedAt);

        Assert.Equal(0, await ReadActiveChatsAsync(seeded.OperatorId));
    }

    [Fact]
    public async Task RunOnceAsync_LeavesAnAssignedConversationAlone_WhenAMessageArrivedInsideTheWindow()
    {
        var widgetWindow = TimeSpan.FromHours(1);
        // The conversation itself is old; only the message is recent - proves the job looks at last
        // message activity, not conversation age.
        var seeded = await SeedAssignedConversationAsync(
            createdAt: Now - widgetWindow - TimeSpan.FromHours(2), holdsCapacityClaim: true);

        await using (var db = fixture.CreateDbContext())
        {
            var conversation = await db.Conversations.Include("_messages")
                .SingleAsync(c => c.Id == seeded.ConversationId);
            conversation.AddVisitorMessage(
                seeded.VisitorId, new MessageId(Guid.NewGuid()), new MessageBody("still here"),
                Now - TimeSpan.FromMinutes(1));
            await db.SaveChangesAsync();
        }

        await CreateJob(new AutoCloseInactiveConversationsJobOptions { WidgetInactivityWindow = widgetWindow })
            .RunOnceAsync(CancellationToken.None);

        await using var verify = fixture.CreateDbContext();
        var conversation2 = await verify.Conversations.SingleAsync(c => c.Id == seeded.ConversationId);
        Assert.Equal(ConversationState.Assigned, conversation2.State);
        Assert.Equal(1, await ReadActiveChatsAsync(seeded.OperatorId));
    }

    /// <summary>`18-06`'s own Done-when: a short widget window and a longer channel window, both
    /// conversations equally stale by age, only the one past its <em>own</em> threshold closes.</summary>
    [Fact]
    public async Task RunOnceAsync_TheWindowDiffersByChannelKind_OnlyTheConversationPastItsOwnWindowCloses()
    {
        var staleFor = TimeSpan.FromMinutes(30);
        var createdAt = Now - staleFor;

        var widget = await SeedAssignedConversationAsync(createdAt, holdsCapacityClaim: false);
        var channel = await SeedAssignedConversationAsync(
            createdAt, holdsCapacityClaim: false, channelKind: ChannelKind.Max, channelAddress: "max-user-1");

        await CreateJob(new AutoCloseInactiveConversationsJobOptions
        {
            WidgetInactivityWindow = TimeSpan.FromMinutes(10),       // shorter than staleFor: past due
            DefaultChannelInactivityWindow = TimeSpan.FromHours(24), // far longer than staleFor: not due
        }).RunOnceAsync(CancellationToken.None);

        await using var verify = fixture.CreateDbContext();
        var widgetConversation = await verify.Conversations.SingleAsync(c => c.Id == widget.ConversationId);
        var channelConversation = await verify.Conversations.SingleAsync(c => c.Id == channel.ConversationId);

        Assert.Equal(ConversationState.Closed, widgetConversation.State);
        Assert.Equal(ConversationState.Assigned, channelConversation.State);
    }

    /// <summary>`18-06`'s hardest Done-when: auto-close a channel-linked conversation, then feed the
    /// same channel identity a new inbound message through the real end-to-end path
    /// (<c>ReceiveChannelMessageHandler</c> -> <c>StartConversationHandler</c> ->
    /// <c>ConversationRepository.GetActiveForVisitorAsync</c>'s own <c>State != Closed</c> filter) and
    /// show a <em>new</em> conversation opens, still linked to the <em>same</em> <c>VisitorId</c> - the
    /// closed conversation is never touched again, not resurrected, not merged into.</summary>
    [Fact]
    public async Task RunOnceAsync_ThenANewInboundMessageFromTheSameChannelIdentity_OpensANewConversation_LinkedToTheSameVisitor()
    {
        var channelWindow = TimeSpan.FromHours(24);
        const string address = "sms-user-1";
        var seeded = await SeedAssignedConversationAsync(
            createdAt: Now - channelWindow - TimeSpan.FromHours(1), holdsCapacityClaim: true,
            channelKind: ChannelKind.Sms, channelAddress: address);

        await CreateJob(new AutoCloseInactiveConversationsJobOptions
        {
            WidgetInactivityWindow = TimeSpan.FromDays(365),
            DefaultChannelInactivityWindow = channelWindow,
        }).RunOnceAsync(CancellationToken.None);

        await using (var verify = fixture.CreateDbContext())
        {
            var closed = await verify.Conversations.SingleAsync(c => c.Id == seeded.ConversationId);
            Assert.Equal(ConversationState.Closed, closed.State);
        }

        Assert.Equal(0, await ReadActiveChatsAsync(seeded.OperatorId));

        await using var db = fixture.CreateDbContext();
        var receiveChannelMessage = new ReceiveChannelMessageHandler(
            new ChannelIdentityRepository(db),
            new VisitorRepository(db),
            new PendingChannelLinkRequestRepository(db),
            new StartConversationHandler(new VisitorRepository(db), new ConversationRepository(db), new SystemClock(), new UuidV7Generator()),
            new SendVisitorMessageHandler(
                new ConversationRepository(db), new FakeRateLimiter(), new MessageSendRateLimitOptions(),
                new SynchronousMessagePipeline(fixture.DataSource)),
            new SystemClock(),
            new UuidV7Generator());

        var result = await receiveChannelMessage.HandleAsync(
            new ReceiveChannelMessage(
                seeded.SiteId, ChannelKind.Sms, new ExternalChannelAddress(address),
                new ExternalMessageId("mid-after-auto-close"), "still there?"),
            CancellationToken.None);

        Assert.True(result.IsSuccess, result.IsFailure ? result.Error!.Value.Message : "");
        Assert.Equal(seeded.VisitorId, result.Value.VisitorId);
        Assert.NotEqual(seeded.ConversationId.Value, result.Value.ConversationId.Value);

        await using var final = fixture.CreateDbContext();
        var oldConversation = await final.Conversations.SingleAsync(c => c.Id == seeded.ConversationId);
        var newConversation = await final.Conversations.SingleAsync(c => c.Id == result.Value.ConversationId);

        // The old row is untouched by the new message - still Closed, still the same visitor - and
        // the new one is a genuinely separate conversation for that same visitor, not a reopen.
        Assert.Equal(ConversationState.Closed, oldConversation.State);
        Assert.Equal(seeded.VisitorId, oldConversation.VisitorId);
        Assert.Equal(seeded.VisitorId, newConversation.VisitorId);
        Assert.NotEqual(ConversationState.Closed, newConversation.State);
    }

    /// <summary>`18-06`'s own scope note, at the job level: a `Waiting` conversation is a queue-depth
    /// problem, never an inactivity one, and this job's query filters `state = 'Assigned'` specifically
    /// so it is never even a candidate - proven here rather than only inferred from reading the query.
    /// </summary>
    [Fact]
    public async Task RunOnceAsync_LeavesAWaitingConversationAlone_RegardlessOfAge()
    {
        var siteId = new SiteId(Guid.NewGuid());
        var visitorId = new VisitorId(Guid.NewGuid());
        var conversationId = new ConversationId(Guid.NewGuid());
        var createdAt = Now - TimeSpan.FromDays(365);

        await using (var db = fixture.CreateDbContext())
        {
            db.Sites.Add(new Site(siteId, $"site_{siteId.Value:N}", []));
            db.Visitors.Add(new Visitor(visitorId, siteId, createdAt));
            db.Conversations.Add(Conversation.Start(conversationId, siteId, visitorId, createdAt));
            await db.SaveChangesAsync();
        }

        await CreateJob(new AutoCloseInactiveConversationsJobOptions { WidgetInactivityWindow = TimeSpan.FromMinutes(1) })
            .RunOnceAsync(CancellationToken.None);

        await using var verify = fixture.CreateDbContext();
        var conversation = await verify.Conversations.SingleAsync(c => c.Id == conversationId);
        Assert.Equal(ConversationState.Waiting, conversation.State);
    }

    private AutoCloseInactiveConversationsJob CreateJob(AutoCloseInactiveConversationsJobOptions options) => new(
        fixture.DataSource,
        new DirectScopeFactory(fixture, new FixedClock(Now)),
        new FixedClock(Now),
        Options.Create(options),
        NullLogger<AutoCloseInactiveConversationsJob>.Instance);

    private async Task<int> ReadActiveChatsAsync(OperatorId operatorId)
    {
        await using var connection = await fixture.DataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand("SELECT active_chats FROM operators WHERE id = @id", connection);
        command.Parameters.AddWithValue("id", operatorId.Value);
        return (int)(await command.ExecuteScalarAsync())!;
    }

    private sealed record Seeded(SiteId SiteId, VisitorId VisitorId, OperatorId OperatorId, ConversationId ConversationId);

    private async Task<Seeded> SeedAssignedConversationAsync(
        DateTimeOffset createdAt, bool holdsCapacityClaim, ChannelKind? channelKind = null, string? channelAddress = null)
    {
        var siteId = new SiteId(Guid.NewGuid());
        var visitorId = new VisitorId(Guid.NewGuid());
        var operatorId = new OperatorId(Guid.NewGuid());
        var conversationId = new ConversationId(Guid.NewGuid());

        await using (var db = fixture.CreateDbContext())
        {
            db.Sites.Add(new Site(siteId, $"site_{siteId.Value:N}", []));
            db.Visitors.Add(new Visitor(visitorId, siteId, createdAt));
            db.Operators.Add(new Operator(operatorId, siteId, OperatorStatus.Online, capacity: 5));

            if (channelKind is { } kind)
            {
                db.ChannelIdentities.Add(ChannelIdentity.Link(
                    new ChannelIdentityId(Guid.NewGuid()), siteId, kind,
                    new ExternalChannelAddress(channelAddress ?? $"addr-{Guid.NewGuid():N}"), visitorId, createdAt));
            }

            var conversation = Conversation.Start(conversationId, siteId, visitorId, createdAt);
            conversation.AssignTo(operatorId, createdAt, holdsCapacityClaim);
            conversation.ClearDomainEvents();
            db.Conversations.Add(conversation);

            await db.SaveChangesAsync();
        }

        if (holdsCapacityClaim)
        {
            // active_chats is a shadow property (4-01) - seed it directly to match the AssignTo call
            // above, since EF never writes it - the same setup OperatorConversationReleaserTests uses.
            await using var connection = await fixture.DataSource.OpenConnectionAsync();
            await using var command = new NpgsqlCommand("UPDATE operators SET active_chats = 1 WHERE id = @id", connection);
            command.Parameters.AddWithValue("id", operatorId.Value);
            await command.ExecuteNonQueryAsync();
        }

        return new Seeded(siteId, visitorId, operatorId, conversationId);
    }

    private sealed class FixedClock(DateTimeOffset now) : IClock
    {
        public DateTimeOffset UtcNow => now;
    }

    /// <summary>
    /// The job's own production shape resolves `AutoCloseConversationHandler` from a fresh
    /// `IServiceScopeFactory` scope per candidate (the job's own remarks on why: a captured scoped
    /// handler in a singleton hosted service would share one `DbContext`/change-tracker across every
    /// close for the life of the process). This test double reproduces exactly that - one fresh
    /// `AgoChatDbContext` per scope, wired to real `ConversationRepository`/`OperatorCapacityStore`/
    /// `EfOutboxWriter` against `fixture`'s real Postgres container - without pulling in a full ASP.NET
    /// Core DI container for a two-line test fake: a real `IServiceProvider` implementation would add
    /// ceremony this scope's one known consumer (`AutoCloseConversationHandler`) does not need.
    /// </summary>
    private sealed class DirectScopeFactory(PostgresFixture fixture, IClock clock) : IServiceScopeFactory
    {
        public IServiceScope CreateScope()
        {
            var db = fixture.CreateDbContext();
            var handler = new AutoCloseConversationHandler(
                new ConversationRepository(db),
                new OperatorCapacityStore(db),
                new EfOutboxWriter<AgoChatDbContext>(db),
                new UuidV7Generator(),
                clock,
                NullLogger<AutoCloseConversationHandler>.Instance);
            return new DirectScope(db, handler);
        }

        private sealed class DirectScope(AgoChatDbContext db, AutoCloseConversationHandler handler) : IServiceScope
        {
            public IServiceProvider ServiceProvider { get; } = new SingleServiceProvider(handler);

            public void Dispose() => db.Dispose();
        }

        private sealed class SingleServiceProvider(object service) : IServiceProvider
        {
            public object? GetService(Type serviceType) => serviceType.IsInstanceOfType(service) ? service : null;
        }
    }
}
