using Ago.Chat.Application.Abstractions;
using Ago.Chat.Domain;
using Ago.Chat.Infrastructure.Postgres;
using Ago.Chat.Worker;

namespace Ago.Chat.Integration.Tests;

/// <summary>
/// `24-10`'s two remaining Done-when claims that only make sense as a scenario, not a single read:
/// "unblocking restores exactly the prior state" (proven here by snapshotting a real read before and
/// after a block/unblock round trip, not asserted), and "blocking must not become a second name for the
/// erasure queue" (proven by driving the real erasure-claim query against a conversation that is
/// blocked but never had erasure requested).
/// </summary>
[Collection(PostgresCollection.Name)]
public class ConversationBlockingScenarioTests(PostgresFixture fixture)
{
    private static readonly DateTimeOffset Now = new(DateTimeOffset.UtcNow.Ticks / TimeSpan.TicksPerSecond * TimeSpan.TicksPerSecond, TimeSpan.Zero);

    private async Task<(SiteId SiteId, ConversationId ConversationId)> SeedConversationWithAMessage()
    {
        var siteId = new SiteId(Guid.NewGuid());
        var visitorId = new VisitorId(Guid.NewGuid());
        var conversation = Conversation.Start(new ConversationId(Guid.NewGuid()), siteId, visitorId, Now);
        conversation.AddVisitorMessage(visitorId, new MessageId(Guid.NewGuid()), new MessageBody("hello"), Now);

        await using var db = fixture.CreateDbContext();
        db.Sites.Add(new Site(siteId, $"site_{siteId.Value:N}", []));
        db.Visitors.Add(new Visitor(visitorId, siteId, Now));
        db.Conversations.Add(conversation);
        await db.SaveChangesAsync();

        return (siteId, conversation.Id);
    }

    /// <summary>The reversibility claim, demonstrated rather than asserted: a real "before" snapshot
    /// from <see cref="IConversationReadStore.GetByIdAsync"/>, a block that makes the same read return
    /// nothing, an unblock, and a fresh "after" snapshot compared field-by-field against the one taken
    /// before anything happened - not merely "a read succeeds again", which a bug that quietly changed
    /// a field on unblock would not catch.</summary>
    [Fact]
    public async Task BlockingThenUnblocking_RestoresExactlyThePriorReadableState()
    {
        var (siteId, conversationId) = await SeedConversationWithAMessage();
        var readStore = new ConversationReadStore(fixture.DataSource);

        var before = await readStore.GetByIdAsync(conversationId, siteId, CancellationToken.None);
        Assert.NotNull(before);

        var blocks = new ConversationBlockRepository(fixture.DataSource);
        var blockOutcome = await blocks.BlockAsync(
            conversationId, siteId, new OperatorId(Guid.NewGuid()), Guid.NewGuid(), Now, CancellationToken.None);
        Assert.Equal(ConversationBlockOutcome.Applied, blockOutcome);

        var whileBlocked = await readStore.GetByIdAsync(conversationId, siteId, CancellationToken.None);
        Assert.Null(whileBlocked);

        var unblockOutcome = await blocks.UnblockAsync(
            conversationId, siteId, new OperatorId(Guid.NewGuid()), Guid.NewGuid(), Now.AddMinutes(5), CancellationToken.None);
        Assert.Equal(ConversationBlockOutcome.Applied, unblockOutcome);

        var after = await readStore.GetByIdAsync(conversationId, siteId, CancellationToken.None);
        Assert.NotNull(after);
        Assert.Equal(before!.Id, after!.Id);
        Assert.Equal(before.VisitorId, after.VisitorId);
        Assert.Equal(before.OperatorId, after.OperatorId);
        Assert.Equal(before.State, after.State);
        Assert.Equal(before.CreatedAt, after.CreatedAt);
        Assert.Equal(before.OperatorUnreadCount, after.OperatorUnreadCount);
        Assert.Equal(before.Outcome, after.Outcome);
        Assert.Equal(before.OperatorName, after.OperatorName);

        // The message history underneath is untouched too - blocking never deletes or rewrites
        // anything, only hides.
        var history = await readStore.GetHistoryAsync(conversationId, siteId, beforeSequence: null, pageSize: 10, CancellationToken.None);
        var message = Assert.Single(history.Messages);
        Assert.Equal("hello", message.Body);
    }

    /// <summary>"Blocking must not become a second name for the erasure queue" - <see
    /// cref="ConversationErasureQuery.ListPendingAsync"/> is the real claim query
    /// <see cref="ConversationErasureJob"/> drives every cycle; a conversation that is blocked but has
    /// never had erasure requested must never appear in it.</summary>
    [Fact]
    public async Task ABlockedConversation_WithNoErasureRequested_IsNeverClaimedByTheErasureQuery()
    {
        var (siteId, conversationId) = await SeedConversationWithAMessage();
        var blocks = new ConversationBlockRepository(fixture.DataSource);
        var outcome = await blocks.BlockAsync(
            conversationId, siteId, new OperatorId(Guid.NewGuid()), Guid.NewGuid(), Now, CancellationToken.None);
        Assert.Equal(ConversationBlockOutcome.Applied, outcome);

        await using var connection = await fixture.DataSource.OpenConnectionAsync();
        var pending = await ConversationErasureQuery.ListPendingAsync(connection, limit: 100, CancellationToken.None);

        Assert.DoesNotContain(pending, p => p.ConversationId == conversationId.Value);
    }

    /// <summary>The converse of the test above, named explicitly so the pair reads as one claim: a
    /// conversation that *is* flagged for erasure is claimed regardless of its block state - blocking
    /// and erasure are two independent flags on the same row, never one standing in for the other.
    /// </summary>
    [Fact]
    public async Task ABlockedConversation_WithErasureAlsoRequested_IsStillClaimedByTheErasureQuery()
    {
        var (siteId, conversationId) = await SeedConversationWithAMessage();
        var blocks = new ConversationBlockRepository(fixture.DataSource);
        await blocks.BlockAsync(conversationId, siteId, new OperatorId(Guid.NewGuid()), Guid.NewGuid(), Now, CancellationToken.None);

        var erasures = new ErasureRequestRepository(fixture.DataSource);
        var requested = await erasures.RequestConversationErasureAsync(
            conversationId, siteId, new OperatorId(Guid.NewGuid()), Guid.NewGuid(), Now.AddMinutes(1), CancellationToken.None);
        Assert.True(requested);

        await using var connection = await fixture.DataSource.OpenConnectionAsync();
        var pending = await ConversationErasureQuery.ListPendingAsync(connection, limit: 100, CancellationToken.None);

        Assert.Contains(pending, p => p.ConversationId == conversationId.Value);
    }
}
