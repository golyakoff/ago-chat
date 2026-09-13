using Ago.Chat.Application.Abstractions;
using Ago.Chat.Domain;
using Ago.Chat.Infrastructure.Postgres;
using Ago.Chat.Infrastructure.Postgres.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Ago.Chat.Integration.Tests;

/// <summary>
/// `23-76`'s direct proof of its own central claim, the identical shape
/// <see cref="ConversationAttachmentBudgetStoreTests"/> already establishes for the sibling column -
/// the atomic <c>UPDATE sites SET attachment_bytes_reserved = attachment_bytes_reserved + @bytes
/// WHERE ... &lt;= @budget</c> never lets the reserved total exceed the tenant's own budget, even
/// under real concurrent load - many real connections from the pool racing the same site row, not
/// sequential awaits pretending to be concurrent.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class SiteAttachmentStorageBudgetStoreTests(PostgresFixture fixture)
{
    /// <summary>This is the test that matters, the identical reasoning
    /// <see cref="ConversationAttachmentBudgetStoreTests"/>'s own concurrency test states for itself:
    /// ten simultaneous presign requests for the same tenant must not together exceed its own storage
    /// budget. Ten real, concurrent <see cref="SiteAttachmentStorageBudgetStore.TryReserveAsync"/>
    /// calls, each declaring 10 MiB against a 30 MiB budget - room for exactly three.</summary>
    [Fact]
    public async Task TryReserveAsync_UnderConcurrentLoad_NeverExceedsTheBudget_AndReservesExactlyAsManyAsFit()
    {
        const long budgetBytes = 30L * 1024 * 1024;
        const long declaredBytes = 10L * 1024 * 1024;
        const int attempts = 10;
        var siteId = await SeedSiteAsync();

        // A fresh AgoChatDbContext per attempt, not one store shared across all of them - the identical
        // "DbContext is not thread-safe" reasoning ConversationAttachmentBudgetStoreTests' own remarks
        // give.
        var results = await Task.WhenAll(Enumerable.Range(0, attempts)
            .Select(async _ =>
            {
                await using var db = fixture.CreateDbContext();
                return await new SiteAttachmentStorageBudgetStore(db).TryReserveAsync(
                    siteId, declaredBytes, budgetBytes, CancellationToken.None);
            }));

        Assert.Equal(3, results.Count(r => r.Reserved));
        Assert.Equal(attempts - 3, results.Count(r => !r.Reserved));
        Assert.Equal(3 * declaredBytes, await ReadReservedBytesAsync(siteId));
        Assert.All(results.Where(r => !r.Reserved), r => Assert.Equal(0, r.RemainingBytes));
    }

    [Fact]
    public async Task TryReserveAsync_WhenThereIsRoom_ReservesAndReturnsTheRemainingBytes()
    {
        var siteId = await SeedSiteAsync();

        AttachmentBudgetResult result;
        await using (var db = fixture.CreateDbContext())
        {
            result = await new SiteAttachmentStorageBudgetStore(db).TryReserveAsync(siteId, 400, 1000, CancellationToken.None);
        }

        Assert.True(result.Reserved);
        Assert.Equal(600, result.RemainingBytes);
        Assert.Equal(400, await ReadReservedBytesAsync(siteId));
    }

    [Fact]
    public async Task TryReserveAsync_WhenARequestWouldExceedTheBudget_RefusesAndLeavesTheTotalUnchanged()
    {
        var siteId = await SeedSiteAsync();
        await using (var db = fixture.CreateDbContext())
        {
            Assert.True((await new SiteAttachmentStorageBudgetStore(db).TryReserveAsync(
                siteId, 900, 1000, CancellationToken.None)).Reserved);
        }

        AttachmentBudgetResult second;
        await using (var db = fixture.CreateDbContext())
        {
            second = await new SiteAttachmentStorageBudgetStore(db).TryReserveAsync(siteId, 200, 1000, CancellationToken.None);
        }

        Assert.False(second.Reserved);
        Assert.Equal(100, second.RemainingBytes);
        Assert.Equal(900, await ReadReservedBytesAsync(siteId));
    }

    [Fact]
    public async Task ReleaseAsync_DecrementsTheReservedTotal_AndNeverGoesBelowZero()
    {
        var siteId = await SeedSiteAsync();
        await using (var db = fixture.CreateDbContext())
        {
            Assert.True((await new SiteAttachmentStorageBudgetStore(db).TryReserveAsync(
                siteId, 500, 1000, CancellationToken.None)).Reserved);
        }

        await using (var db = fixture.CreateDbContext())
        {
            await new SiteAttachmentStorageBudgetStore(db).ReleaseAsync(siteId, 500, CancellationToken.None);
        }
        Assert.Equal(0, await ReadReservedBytesAsync(siteId));

        // A duplicate/racing release must not push the total negative.
        await using (var db = fixture.CreateDbContext())
        {
            await new SiteAttachmentStorageBudgetStore(db).ReleaseAsync(siteId, 500, CancellationToken.None);
        }
        Assert.Equal(0, await ReadReservedBytesAsync(siteId));
    }

    private async Task<SiteId> SeedSiteAsync()
    {
        var siteId = new SiteId(Guid.NewGuid());

        await using var db = fixture.CreateDbContext();
        db.Sites.Add(new Site(siteId, $"site_{siteId.Value:N}", []));
        await db.SaveChangesAsync();

        return siteId;
    }

    private async Task<long> ReadReservedBytesAsync(SiteId siteId)
    {
        await using var connection = await fixture.DataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand(
            "SELECT attachment_bytes_reserved FROM sites WHERE id = @id", connection);
        command.Parameters.AddWithValue("id", siteId.Value);
        return (long)(await command.ExecuteScalarAsync())!;
    }
}
