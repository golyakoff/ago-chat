using Ago.Chat.Application.Abstractions;
using Ago.Chat.Domain;
using Ago.Chat.Infrastructure.Postgres;
using Ago.Chat.Infrastructure.Postgres.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Ago.Chat.Integration.Tests;

/// <summary>
/// `23-75`'s direct proof of its own central claim: the atomic
/// <c>UPDATE conversations SET attachment_bytes_reserved = attachment_bytes_reserved + @bytes
/// WHERE ... &lt;= @budget</c> never lets the reserved total exceed the budget, even under real
/// concurrent load - many real connections from the pool racing the same conversation row, not
/// sequential awaits pretending to be concurrent. The identical proof shape
/// <c>OperatorCapacityStoreTests.TryClaimAsync_UnderConcurrentLoad_...</c> already established for
/// <c>operators.active_chats</c>, applied to this item's own column.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class ConversationAttachmentBudgetStoreTests(PostgresFixture fixture)
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    /// <summary>This is the test that matters (`23-75`'s own words): ten simultaneous presign
    /// requests must not together exceed the conversation's budget. Ten real, concurrent
    /// <see cref="ConversationAttachmentBudgetStore.TryReserveAsync"/> calls, each declaring 10 MiB
    /// against a 30 MiB budget - room for exactly three. A sequential loop over the same ten calls
    /// would pass even with a plain "read the total, compare, then write" implementation racing
    /// itself; only real concurrent load exercises the compare-and-set this item's design depends on.
    /// </summary>
    [Fact]
    public async Task TryReserveAsync_UnderConcurrentLoad_NeverExceedsTheBudget_AndReservesExactlyAsManyAsFit()
    {
        const long budgetBytes = 30L * 1024 * 1024;
        const long declaredBytes = 10L * 1024 * 1024;
        const int attempts = 10;
        var conversationId = await SeedConversationAsync();

        // A fresh AgoChatDbContext per attempt, not one store shared across all of them -
        // OperatorCapacityStoreTests' own remarks: DbContext is not thread-safe, and a scoped-per-
        // request DbContext is exactly how this port is actually used in production
        // (CreateAttachmentHandler's own per-request scope).
        var results = await Task.WhenAll(Enumerable.Range(0, attempts)
            .Select(async _ =>
            {
                await using var db = fixture.CreateDbContext();
                return await new ConversationAttachmentBudgetStore(db).TryReserveAsync(
                    conversationId, declaredBytes, budgetBytes, CancellationToken.None);
            }));

        Assert.Equal(3, results.Count(r => r.Reserved));
        Assert.Equal(attempts - 3, results.Count(r => !r.Reserved));
        Assert.Equal(3 * declaredBytes, await ReadReservedBytesAsync(conversationId));

        // Every refusal's own remaining-budget figure must agree with the real final state - `23-75`'s
        // "the refusal names the remaining budget" requirement, checked under the exact concurrent
        // load the design has to survive, not only sequentially.
        Assert.All(results.Where(r => !r.Reserved), r => Assert.Equal(0, r.RemainingBytes));
    }

    [Fact]
    public async Task TryReserveAsync_WhenThereIsRoom_ReservesAndReturnsTheRemainingBytes()
    {
        var conversationId = await SeedConversationAsync();

        AttachmentBudgetResult result;
        await using (var db = fixture.CreateDbContext())
        {
            result = await new ConversationAttachmentBudgetStore(db).TryReserveAsync(
                conversationId, 400, 1000, CancellationToken.None);
        }

        Assert.True(result.Reserved);
        Assert.Equal(600, result.RemainingBytes);
        Assert.Equal(400, await ReadReservedBytesAsync(conversationId));
    }

    [Fact]
    public async Task TryReserveAsync_WhenARequestWouldExceedTheBudget_RefusesAndLeavesTheTotalUnchanged()
    {
        var conversationId = await SeedConversationAsync();
        await using (var db = fixture.CreateDbContext())
        {
            Assert.True((await new ConversationAttachmentBudgetStore(db).TryReserveAsync(
                conversationId, 900, 1000, CancellationToken.None)).Reserved);
        }

        AttachmentBudgetResult second;
        await using (var db = fixture.CreateDbContext())
        {
            second = await new ConversationAttachmentBudgetStore(db).TryReserveAsync(
                conversationId, 200, 1000, CancellationToken.None);
        }

        Assert.False(second.Reserved);
        Assert.Equal(100, second.RemainingBytes);
        Assert.Equal(900, await ReadReservedBytesAsync(conversationId));
    }

    [Fact]
    public async Task ReleaseAsync_DecrementsTheReservedTotal_AndNeverGoesBelowZero()
    {
        var conversationId = await SeedConversationAsync();
        await using (var db = fixture.CreateDbContext())
        {
            Assert.True((await new ConversationAttachmentBudgetStore(db).TryReserveAsync(
                conversationId, 500, 1000, CancellationToken.None)).Reserved);
        }

        await using (var db = fixture.CreateDbContext())
        {
            await new ConversationAttachmentBudgetStore(db).ReleaseAsync(conversationId, 500, CancellationToken.None);
        }
        Assert.Equal(0, await ReadReservedBytesAsync(conversationId));

        // A duplicate/racing release must not push the total negative.
        await using (var db = fixture.CreateDbContext())
        {
            await new ConversationAttachmentBudgetStore(db).ReleaseAsync(conversationId, 500, CancellationToken.None);
        }
        Assert.Equal(0, await ReadReservedBytesAsync(conversationId));
    }

    private async Task<ConversationId> SeedConversationAsync()
    {
        var siteId = new SiteId(Guid.NewGuid());
        var visitorId = new VisitorId(Guid.NewGuid());
        var conversationId = new ConversationId(Guid.NewGuid());

        await using var db = fixture.CreateDbContext();
        db.Sites.Add(new Site(siteId, $"site_{siteId.Value:N}", []));
        db.Visitors.Add(new Visitor(visitorId, siteId, Now));
        db.Conversations.Add(Conversation.Start(conversationId, siteId, visitorId, Now));
        await db.SaveChangesAsync();

        return conversationId;
    }

    private async Task<long> ReadReservedBytesAsync(ConversationId conversationId)
    {
        await using var connection = await fixture.DataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand(
            "SELECT attachment_bytes_reserved FROM conversations WHERE id = @id", connection);
        command.Parameters.AddWithValue("id", conversationId.Value);
        return (long)(await command.ExecuteScalarAsync())!;
    }
}
