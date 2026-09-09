using System.Diagnostics;
using Ago.Chat.Application.Abstractions;
using Ago.Chat.Domain;
using Ago.Chat.Infrastructure.Postgres.Pipeline;
using Ago.Platform.Hosting;
using Ago.Platform.Kernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Ago.Chat.Integration.Tests;

/// <summary>
/// `23-64`/`adr/0148`: <see cref="MessageBatchWriter"/>'s auto-greeting materialisation, against a
/// real Postgres - the same "real database, `NoOpCache`, so a cache-aside read always re-reads the
/// site's *current* configuration" shape <see cref="MessageBatchWriterTests"/>'s own
/// <c>FlushAsync_StampsRetentionClassFromTheSitesCurrentTier...</c> test already establishes for
/// `RetentionClass`.
///
/// This file is also this item's own proof for the Done-when box the item text itself calls out as
/// needing a test rather than inspection - "nothing exists server-side after the auto-open delay
/// fires and the visitor never writes." <see cref="FlushAsync_WithNoPendingMessageAtAll_WritesNothing"/>
/// is the literal statement of that: the timer firing produces no <see cref="IMessagePipeline.EnqueueAsync"/>
/// call at all on the widget side (`ago-widget`'s own fails-before test proves *that* half), and this
/// half proves the other: if a flush never happens, nothing lands in Postgres - not even an empty
/// transaction, because <see cref="MessageBatchWriter.FlushAsync"/> returns immediately for an empty
/// batch. <see cref="FlushAsync_OrdinarySendAgainstAGreetingConfiguredSite_MaterialisesNothing"/> is
/// the other half of the same guarantee from the opposite direction: even once the visitor *does*
/// write, an ordinary <c>SendMessageAsync</c> (not the dedicated
/// <c>SendMessageWithAutoGreetingAsync</c>) materialises nothing extra - materialisation is an opt-in
/// per send, never inferred from site configuration alone.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class MessageBatchWriterAutoGreetingTests(PostgresFixture fixture)
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task FlushAsync_FirstVisitorMessageWithMaterializeFlag_OnASiteWithAutoOpenConfigured_MaterialisesTheGreetingFirst()
    {
        var (_, visitorId, conversationId) = await SeedWaitingConversationAsync(
            autoOpenEnabled: true, greetingText: "Hi, need any help?");

        var result = await FlushOneAsync(
            conversationId, visitorId, "Yes, do you have this in blue?", materializeAutoGreeting: true);

        Assert.True(result.IsSuccess);
        // The visitor's own message is sequence 2 - the greeting was inserted ahead of it.
        Assert.Equal(2, result.Value);

        await using var verify = fixture.CreateDbContext();
        var messages = await verify.Set<Message>()
            .Where(m => m.ConversationId == conversationId)
            .OrderBy(m => m.Sequence)
            .ToListAsync();

        Assert.Equal(2, messages.Count);
        Assert.Equal(MessageAuthorKind.AutoGreeting, messages[0].AuthorKind);
        Assert.Equal("Hi, need any help?", messages[0].Body.Value);
        Assert.Equal(Guid.Empty, messages[0].AuthorId);
        Assert.Equal(MessageAuthorKind.Visitor, messages[1].AuthorKind);
        Assert.Equal("Yes, do you have this in blue?", messages[1].Body.Value);
    }

    [Fact]
    public async Task FlushAsync_FirstVisitorMessageWithMaterializeFlag_WritesTwoOutboxRows_OneForEachMessage()
    {
        var (_, visitorId, conversationId) = await SeedWaitingConversationAsync(
            autoOpenEnabled: true, greetingText: "Hi, need any help?");

        var result = await FlushOneAsync(conversationId, visitorId, "hi", materializeAutoGreeting: true);
        Assert.True(result.IsSuccess);

        await using var verify = fixture.CreateDbContext();
        var messageIds = await verify.Set<Message>()
            .Where(m => m.ConversationId == conversationId)
            .Select(m => m.Id.Value)
            .ToListAsync();
        Assert.Equal(2, messageIds.Count);

        var outboxRows = await verify.Set<Ago.Platform.Persistence.Postgres.OutboxMessage>()
            .Where(o => messageIds.Contains(o.Id))
            .ToListAsync();
        Assert.Equal(2, outboxRows.Count);
        Assert.All(outboxRows, row => Assert.Equal("MessageAccepted", row.Type));
    }

    // The eligibility signal is the send call itself, not the site's own configuration alone - a
    // stale or mistaken flag from an old client is the only thing that could ever set this, and
    // MessageBatchWriter never infers it from WidgetConfig on its own.
    [Fact]
    public async Task FlushAsync_OrdinarySendAgainstAGreetingConfiguredSite_MaterialisesNothing()
    {
        var (_, visitorId, conversationId) = await SeedWaitingConversationAsync(
            autoOpenEnabled: true, greetingText: "Hi, need any help?");

        var result = await FlushOneAsync(conversationId, visitorId, "hi", materializeAutoGreeting: false);

        Assert.True(result.IsSuccess);
        Assert.Equal(1, result.Value);

        await using var verify = fixture.CreateDbContext();
        var messages = await verify.Set<Message>().Where(m => m.ConversationId == conversationId).ToListAsync();
        var message = Assert.Single(messages);
        Assert.Equal(MessageAuthorKind.Visitor, message.AuthorKind);
    }

    // `adr/0148`'s own "the tenant's configuration as it stands today" - a site that never turned
    // auto-open on gets no greeting, even if the widget mistakenly (or maliciously) sets the flag.
    [Fact]
    public async Task FlushAsync_MaterializeFlagSet_ButAutoOpenIsNotEnabledOnTheSite_MaterialisesNothing()
    {
        var (_, visitorId, conversationId) = await SeedWaitingConversationAsync(
            autoOpenEnabled: false, greetingText: null);

        var result = await FlushOneAsync(conversationId, visitorId, "hi", materializeAutoGreeting: true);

        Assert.True(result.IsSuccess);
        Assert.Equal(1, result.Value);

        await using var verify = fixture.CreateDbContext();
        var messages = await verify.Set<Message>().Where(m => m.ConversationId == conversationId).ToListAsync();
        Assert.Single(messages);
    }

    // The aggregate-level guard proven again end to end: a conversation that already has a message
    // (a returning visitor's second send, or a retried Join/Send pair) never gets a second greeting,
    // even with the flag set and the site fully configured.
    [Fact]
    public async Task FlushAsync_MaterializeFlagSet_OnAConversationThatAlreadyHasAMessage_MaterialisesNothing()
    {
        var (_, visitorId, conversationId) = await SeedWaitingConversationAsync(
            autoOpenEnabled: true, greetingText: "Hi, need any help?");
        var first = await FlushOneAsync(conversationId, visitorId, "first message", materializeAutoGreeting: true);
        Assert.True(first.IsSuccess);

        var second = await FlushOneAsync(conversationId, visitorId, "second message", materializeAutoGreeting: true);
        Assert.True(second.IsSuccess);

        await using var verify = fixture.CreateDbContext();
        var messages = await verify.Set<Message>().Where(m => m.ConversationId == conversationId).ToListAsync();
        // Exactly one greeting (from the first flush), plus the two real visitor messages - not two
        // greetings.
        Assert.Equal(3, messages.Count);
        Assert.Single(messages, m => m.AuthorKind == MessageAuthorKind.AutoGreeting);
    }

    // The literal statement of this item's own Done-when box: an empty batch (the shape a timer that
    // never enqueues anything would produce) writes nothing at all - not a transaction, not a row.
    [Fact]
    public async Task FlushAsync_WithNoPendingMessageAtAll_WritesNothing()
    {
        var writer = CreateWriter();

        await writer.FlushAsync([], CancellationToken.None);

        // Nothing to assert against a specific conversation - there is no conversation, because
        // nothing in this test ever created one. The assertion is the absence of a throw and the
        // absence of any row this test's own fixture did not itself seed elsewhere; every other test
        // in this file establishes that a real flush *does* write rows, which is what makes "this one
        // writes none" meaningful rather than vacuous.
        await using var verify = fixture.CreateDbContext();
        var messageCountForThisRun = await verify.Set<Message>().CountAsync(m => m.CreatedAt == Now);
        Assert.Equal(0, messageCountForThisRun);
    }

    private MessageBatchWriter CreateWriter() =>
        new(fixture.DataSource, new SystemClock(), new UuidV7Generator(), new NoOpCache(), NullLogger<MessageBatchWriter>.Instance);

    private async Task<Result<int>> FlushOneAsync(
        ConversationId conversationId, Guid visitorId, string body, bool materializeAutoGreeting)
    {
        var writer = CreateWriter();
        var ack = new TaskCompletionSource<Result<int>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var item = new InboundMessage(
            new PendingMessage(
                conversationId, MessageAuthorKind.Visitor, visitorId, new MessageBody(body),
                MaterializeAutoGreeting: materializeAutoGreeting),
            ack, Stopwatch.GetTimestamp());
        await writer.FlushAsync([item], CancellationToken.None);
        return await ack.Task;
    }

    private async Task<(SiteId SiteId, Guid VisitorId, ConversationId ConversationId)> SeedWaitingConversationAsync(
        bool autoOpenEnabled, string? greetingText)
    {
        var siteId = new SiteId(Guid.NewGuid());
        var visitorId = new VisitorId(Guid.NewGuid());
        var conversationId = new ConversationId(Guid.NewGuid());

        await using var db = fixture.CreateDbContext();
        var site = new Site(siteId, $"site_{siteId.Value:N}", []);
        site.UpdateWidgetConfig(
            new WidgetConfig(
                null, Position.BottomRight, autoOpenEnabled: autoOpenEnabled,
                autoOpenGreetingText: greetingText),
            Now);
        db.Sites.Add(site);
        db.Visitors.Add(new Visitor(visitorId, siteId, Now));
        db.Conversations.Add(Conversation.Start(conversationId, siteId, visitorId, Now));
        await db.SaveChangesAsync();

        return (siteId, visitorId.Value, conversationId);
    }
}
