using Ago.Chat.Domain;
using Ago.Chat.Infrastructure.Postgres;

namespace Ago.Chat.Integration.Tests;

/// <summary>
/// This item's own console screen needs <see cref="ExportRequestRepository.ListForSiteAsync"/>'s real
/// SQL proven, not only <c>FakeExportRequestRepository</c>'s in-memory mirror of it - ordering and
/// cross-tenant scoping are exactly the two properties a fake can silently get right for the wrong
/// reason (an unordered dictionary that happens to enumerate in insertion order, say) while the real
/// <c>ORDER BY</c>/<c>WHERE site_id = @siteId</c> either does or does not hold against a real
/// Postgres.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class ExportRequestRepositoryListForSiteTests(PostgresFixture fixture)
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task ListForSiteAsync_ReturnsOnlyThisSitesRequests_NewestFirst()
    {
        var siteId = await SeedSiteAsync();
        var otherSiteId = await SeedSiteAsync();

        var repository = new ExportRequestRepository(fixture.DataSource);
        var operatorId = new OperatorId(Guid.NewGuid());

        var firstExportId = Guid.NewGuid();
        Assert.True(await repository.CreateAsync(firstExportId, siteId, operatorId, Now, CancellationToken.None));
        var secondExportId = Guid.NewGuid();
        Assert.True(await repository.CreateAsync(secondExportId, siteId, operatorId, Now.AddMinutes(5), CancellationToken.None));

        // A different site's own export - must never appear in siteId's own list.
        Assert.True(await repository.CreateAsync(Guid.NewGuid(), otherSiteId, operatorId, Now.AddMinutes(10), CancellationToken.None));

        var results = await repository.ListForSiteAsync(siteId, CancellationToken.None);

        Assert.Equal(2, results.Count);
        Assert.Equal(secondExportId, results[0].Id); // newest (later RequestedAt) first
        Assert.Equal(firstExportId, results[1].Id);
        Assert.All(results, r => Assert.NotEqual(Guid.Empty, r.Id));
    }

    [Fact]
    public async Task ListForSiteAsync_ForASiteWithNoExports_ReturnsAnEmptyList()
    {
        var siteId = await SeedSiteAsync();
        var repository = new ExportRequestRepository(fixture.DataSource);

        var results = await repository.ListForSiteAsync(siteId, CancellationToken.None);

        Assert.Empty(results);
    }

    private async Task<SiteId> SeedSiteAsync()
    {
        var siteId = new SiteId(Guid.NewGuid());
        await using var db = fixture.CreateDbContext();
        db.Sites.Add(new Site(siteId, $"site_{siteId.Value:N}", []));
        await db.SaveChangesAsync();
        return siteId;
    }
}
