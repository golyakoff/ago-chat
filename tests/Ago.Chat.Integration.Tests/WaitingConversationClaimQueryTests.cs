using Ago.Chat.Domain;
using Ago.Chat.Worker;

namespace Ago.Chat.Integration.Tests;

/// <summary>
/// `4-01`'s direct proof that <see cref="WaitingConversationClaimQuery"/> actually behaves like a
/// `SKIP LOCKED` claim, not just that its SQL parses - two real, concurrently open transactions
/// against the same site's waiting rows, not one transaction called twice sequentially.
/// </summary>
[Collection(PostgresCollection.Name)]
public class WaitingConversationClaimQueryTests(PostgresFixture fixture)
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task ClaimBatchAsync_ReturnsWaitingConversationsForTheSite_OldestFirst_UpToBatchSize()
    {
        var siteId = new SiteId(Guid.NewGuid());
        var expectedOrder = await SeedWaitingConversationsAsync(siteId, count: 3);

        await using var connection = await fixture.DataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();

        var claimed = await WaitingConversationClaimQuery.ClaimBatchAsync(
            connection, transaction, siteId, batchSize: 2, CancellationToken.None);

        Assert.Equal(expectedOrder.Take(2), claimed);
        await transaction.CommitAsync();
    }

    [Fact]
    public async Task ClaimBatchAsync_IgnoresAssignedAndClosedConversations_AndOtherSites()
    {
        var siteId = new SiteId(Guid.NewGuid());
        var otherSiteId = new SiteId(Guid.NewGuid());
        var operatorId = new OperatorId(Guid.NewGuid());
        var waitingId = (await SeedWaitingConversationsAsync(siteId, count: 1)).Single();

        await using (var db = fixture.CreateDbContext())
        {
            db.Operators.Add(new Operator(operatorId, siteId, OperatorStatus.Online, capacity: 5));
            var visitorId = new VisitorId(Guid.NewGuid());
            db.Visitors.Add(new Visitor(visitorId, siteId, Now));
            var assigned = Conversation.Start(new ConversationId(Guid.NewGuid()), siteId, visitorId, Now);
            assigned.AssignTo(operatorId, Now);
            db.Conversations.Add(assigned);

            var closedVisitorId = new VisitorId(Guid.NewGuid());
            db.Visitors.Add(new Visitor(closedVisitorId, siteId, Now));
            var closed = Conversation.Start(new ConversationId(Guid.NewGuid()), siteId, closedVisitorId, Now);
            closed.Close(Now);
            db.Conversations.Add(closed);

            db.Sites.Add(new Site(otherSiteId, $"site_{otherSiteId.Value:N}", []));
            var otherVisitorId = new VisitorId(Guid.NewGuid());
            db.Visitors.Add(new Visitor(otherVisitorId, otherSiteId, Now));
            db.Conversations.Add(Conversation.Start(new ConversationId(Guid.NewGuid()), otherSiteId, otherVisitorId, Now));

            await db.SaveChangesAsync();
        }

        await using var connection = await fixture.DataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();

        var claimed = await WaitingConversationClaimQuery.ClaimBatchAsync(
            connection, transaction, siteId, batchSize: 10, CancellationToken.None);

        Assert.Equal([waitingId], claimed);
        await transaction.CommitAsync();
    }

    [Fact]
    public async Task ClaimBatchAsync_SkipsRowsAlreadyLockedByAnotherOpenTransaction()
    {
        var siteId = new SiteId(Guid.NewGuid());
        var ids = await SeedWaitingConversationsAsync(siteId, count: 3);

        await using var connectionA = await fixture.DataSource.OpenConnectionAsync();
        await using var transactionA = await connectionA.BeginTransactionAsync();
        var claimedByA = await WaitingConversationClaimQuery.ClaimBatchAsync(
            connectionA, transactionA, siteId, batchSize: 2, CancellationToken.None);
        Assert.Equal(2, claimedByA.Count);

        // transactionA is still open (not committed) - its claim's row locks are still held, so a
        // second, concurrently open transaction must skip them rather than blocking or double-claiming.
        await using var connectionB = await fixture.DataSource.OpenConnectionAsync();
        await using var transactionB = await connectionB.BeginTransactionAsync();
        var claimedByB = await WaitingConversationClaimQuery.ClaimBatchAsync(
            connectionB, transactionB, siteId, batchSize: 10, CancellationToken.None);

        Assert.Equal([ids[2]], claimedByB);
        Assert.Empty(claimedByA.Intersect(claimedByB));

        await transactionB.CommitAsync();
        await transactionA.CommitAsync();
    }

    private async Task<List<ConversationId>> SeedWaitingConversationsAsync(SiteId siteId, int count)
    {
        var ids = new List<ConversationId>();
        await using var db = fixture.CreateDbContext();
        db.Sites.Add(new Site(siteId, $"site_{siteId.Value:N}", []));

        for (var i = 0; i < count; i++)
        {
            var visitorId = new VisitorId(Guid.NewGuid());
            var conversationId = new ConversationId(Guid.NewGuid());
            db.Visitors.Add(new Visitor(visitorId, siteId, Now.AddSeconds(i)));
            db.Conversations.Add(Conversation.Start(conversationId, siteId, visitorId, Now.AddSeconds(i)));
            ids.Add(conversationId);
        }

        await db.SaveChangesAsync();
        return ids;
    }
}
