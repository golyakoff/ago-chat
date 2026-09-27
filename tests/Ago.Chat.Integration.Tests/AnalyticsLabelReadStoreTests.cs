using Ago.Chat.Domain;
using Ago.Chat.Infrastructure.Postgres;

namespace Ago.Chat.Integration.Tests;

/// <summary>
/// `26-237`/`adr/0186` §8.1: the operational-side label resolver the analytics report handlers use to merge
/// display names onto the id-keyed rollup rows (the application-layer alternative to the forbidden cross-
/// database join). Proves <see cref="AnalyticsLabelReadStore"/> returns operator/tag names by id, scoped to
/// the site, omits an operator with no display name and any id with no row, and never touches the database
/// for an empty id set.
/// </summary>
[Collection(PostgresCollection.Name)]
public class AnalyticsLabelReadStoreTests(PostgresFixture fixture)
{
    private static readonly DateTimeOffset Now = new(2026, 6, 15, 12, 0, 0, TimeSpan.Zero);

    private AnalyticsLabelReadStore Store => new(fixture.DataSource);

    [Fact]
    public async Task GetOperatorDisplayNamesAsync_ReturnsNamedOperators_OmitsNullNamedAndMissingAndOtherSites()
    {
        var siteId = await CreateSiteAsync();
        var otherSiteId = await CreateSiteAsync();
        var named = new OperatorId(Guid.NewGuid());
        var noName = new OperatorId(Guid.NewGuid());
        var otherSiteOperator = new OperatorId(Guid.NewGuid());
        var missing = new OperatorId(Guid.NewGuid());

        await using (var db = fixture.CreateDbContext())
        {
            db.Operators.Add(new Operator(named, siteId, OperatorStatus.Offline, capacity: 5, displayName: "Alice"));
            db.Operators.Add(new Operator(noName, siteId, OperatorStatus.Offline, capacity: 5, displayName: null));
            db.Operators.Add(new Operator(otherSiteOperator, otherSiteId, OperatorStatus.Offline, capacity: 5, displayName: "Bob"));
            await db.SaveChangesAsync();
        }

        var names = await Store.GetOperatorDisplayNamesAsync(
            siteId, [named, noName, otherSiteOperator, missing], CancellationToken.None);

        Assert.Equal("Alice", names[named]);
        Assert.False(names.ContainsKey(noName));            // null display name -> omitted
        Assert.False(names.ContainsKey(otherSiteOperator)); // belongs to another site -> not addressable
        Assert.False(names.ContainsKey(missing));           // no row -> omitted
    }

    [Fact]
    public async Task GetTagNamesAsync_ReturnsNamedTags_OmitsMissingAndOtherSites()
    {
        var siteId = await CreateSiteAsync();
        var otherSiteId = await CreateSiteAsync();
        var billing = new TagId(Guid.NewGuid());
        var otherSiteTag = new TagId(Guid.NewGuid());
        var missing = new TagId(Guid.NewGuid());

        await using (var db = fixture.CreateDbContext())
        {
            db.Tags.Add(Tag.Create(billing, siteId, "Billing", Now));
            db.Tags.Add(Tag.Create(otherSiteTag, otherSiteId, "Refund", Now));
            await db.SaveChangesAsync();
        }

        var names = await Store.GetTagNamesAsync(siteId, [billing, otherSiteTag, missing], CancellationToken.None);

        Assert.Equal("Billing", names[billing]);
        Assert.False(names.ContainsKey(otherSiteTag));
        Assert.False(names.ContainsKey(missing));
    }

    [Fact]
    public async Task EmptyIdSets_ReturnEmptyMaps()
    {
        var siteId = await CreateSiteAsync();

        Assert.Empty(await Store.GetOperatorDisplayNamesAsync(siteId, [], CancellationToken.None));
        Assert.Empty(await Store.GetTagNamesAsync(siteId, [], CancellationToken.None));
    }

    private async Task<SiteId> CreateSiteAsync()
    {
        var siteId = new SiteId(Guid.NewGuid());
        await using var db = fixture.CreateDbContext();
        db.Sites.Add(new Site(siteId, $"site_{siteId.Value:N}", []));
        await db.SaveChangesAsync();
        return siteId;
    }
}
