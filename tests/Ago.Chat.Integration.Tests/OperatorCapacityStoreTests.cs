using Ago.Chat.Domain;
using Ago.Chat.Infrastructure.Postgres;
using Npgsql;

namespace Ago.Chat.Integration.Tests;

/// <summary>
/// `4-01`'s direct proof of `concurrency.md`'s "Operator assignment" claim: the atomic
/// `UPDATE ... WHERE active_chats &lt; capacity` never lets `active_chats` exceed `capacity`, even
/// under real concurrent load - many real connections from the pool racing the same row, not
/// sequential awaits pretending to be concurrent (`4-01`'s own Done when says exactly this).
/// </summary>
[Collection(PostgresCollection.Name)]
public class OperatorCapacityStoreTests(PostgresFixture fixture)
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task TryClaimAsync_UnderConcurrentLoad_NeverExceedsCapacity_AndClaimsExactlyCapacityMany()
    {
        const int capacity = 5;
        const int attempts = 20;
        var (siteId, operatorId) = await SeedOperatorAsync(capacity);
        var store = new OperatorCapacityStore(fixture.DataSource);

        var results = await Task.WhenAll(Enumerable.Range(0, attempts)
            .Select(_ => store.TryClaimAsync(operatorId, CancellationToken.None)));

        Assert.Equal(capacity, results.Count(claimed => claimed));
        Assert.Equal(attempts - capacity, results.Count(claimed => !claimed));
        Assert.Equal(capacity, await ReadActiveChatsAsync(operatorId));
    }

    [Fact]
    public async Task ReleaseAsync_DecrementsActiveChats_AndNeverGoesBelowZero()
    {
        const int capacity = 2;
        var (siteId, operatorId) = await SeedOperatorAsync(capacity);
        var store = new OperatorCapacityStore(fixture.DataSource);
        Assert.True(await store.TryClaimAsync(operatorId, CancellationToken.None));

        await store.ReleaseAsync(operatorId, CancellationToken.None);
        Assert.Equal(0, await ReadActiveChatsAsync(operatorId));

        // A duplicate/racing release must not push the count negative.
        await store.ReleaseAsync(operatorId, CancellationToken.None);
        Assert.Equal(0, await ReadActiveChatsAsync(operatorId));
    }

    [Fact]
    public async Task TryClaimAsync_WhenAlreadyAtCapacity_ReturnsFalse_AndLeavesActiveChatsUnchanged()
    {
        const int capacity = 1;
        var (siteId, operatorId) = await SeedOperatorAsync(capacity);
        var store = new OperatorCapacityStore(fixture.DataSource);
        Assert.True(await store.TryClaimAsync(operatorId, CancellationToken.None));

        var secondClaim = await store.TryClaimAsync(operatorId, CancellationToken.None);

        Assert.False(secondClaim);
        Assert.Equal(1, await ReadActiveChatsAsync(operatorId));
    }

    private async Task<(SiteId SiteId, OperatorId OperatorId)> SeedOperatorAsync(int capacity)
    {
        var siteId = new SiteId(Guid.NewGuid());
        var operatorId = new OperatorId(Guid.NewGuid());

        await using var db = fixture.CreateDbContext();
        db.Sites.Add(new Site(siteId, $"site_{siteId.Value:N}", []));
        db.Operators.Add(new Operator(operatorId, siteId, OperatorStatus.Online, capacity));
        await db.SaveChangesAsync();

        return (siteId, operatorId);
    }

    private async Task<int> ReadActiveChatsAsync(OperatorId operatorId)
    {
        await using var connection = await fixture.DataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand("SELECT active_chats FROM operators WHERE id = @id", connection);
        command.Parameters.AddWithValue("id", operatorId.Value);
        return (int)(await command.ExecuteScalarAsync())!;
    }
}
