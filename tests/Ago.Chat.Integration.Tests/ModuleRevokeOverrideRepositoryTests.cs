using Ago.Chat.Domain;
using Ago.Chat.Infrastructure.Postgres;

namespace Ago.Chat.Integration.Tests;

/// <summary>
/// `23-13`: proves the one guarantee no fake can stand in for (`testing.md`) - a real round trip
/// against Postgres, with no `sites` foreign key to violate
/// (`ModuleRevokeOverrideEntityConfiguration`'s own remarks), and rows for two different sites never
/// bleed into one tenant's own list.
/// </summary>
[Collection(PostgresCollection.Name)]
public class ModuleRevokeOverrideRepositoryTests(PostgresFixture fixture)
{
    private static readonly DateTimeOffset RevokedAt = new(2026, 9, 5, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task RecordAsync_ThenListForSiteAsync_RoundTripsTheRow()
    {
        var siteId = new SiteId(Guid.NewGuid());
        var repository = new ModuleRevokeOverrideRepository(fixture.DataSource);

        await repository.RecordAsync(
            Guid.NewGuid(), siteId, "calendar", "keycloak-sub-of-the-owner",
            "Tenant under active investigation; access must stop now.", RevokedAt, CancellationToken.None);

        var recorded = Assert.Single(await repository.ListForSiteAsync(siteId, CancellationToken.None));
        Assert.Equal(siteId, recorded.SiteId);
        Assert.Equal("calendar", recorded.ModuleKey);
        Assert.Equal("keycloak-sub-of-the-owner", recorded.RevokedBy);
        Assert.Equal("Tenant under active investigation; access must stop now.", recorded.Reason);
        Assert.Equal(RevokedAt, recorded.RevokedAt);
    }

    /// <summary>No foreign key to `sites` - a row can be written for a site id that was never
    /// registered at all, the same deliberate absence `access_records`/`erasure_records` already
    /// establish for the identical "must survive the tenant's own eventual erasure" reason.</summary>
    [Fact]
    public async Task RecordAsync_ForASiteWithNoRowAtAll_StillSucceeds()
    {
        var neverRegisteredSiteId = new SiteId(Guid.NewGuid());
        var repository = new ModuleRevokeOverrideRepository(fixture.DataSource);

        await repository.RecordAsync(
            Guid.NewGuid(), neverRegisteredSiteId, "calendar", "keycloak-sub-of-the-owner", "a reason",
            RevokedAt, CancellationToken.None);

        Assert.Single(await repository.ListForSiteAsync(neverRegisteredSiteId, CancellationToken.None));
    }

    [Fact]
    public async Task ListForSiteAsync_NeverReturnsAnotherSitesOwnOverrides()
    {
        var siteA = new SiteId(Guid.NewGuid());
        var siteB = new SiteId(Guid.NewGuid());
        var repository = new ModuleRevokeOverrideRepository(fixture.DataSource);

        await repository.RecordAsync(
            Guid.NewGuid(), siteA, "calendar", "keycloak-sub-of-the-owner", "site A's own reason", RevokedAt,
            CancellationToken.None);
        await repository.RecordAsync(
            Guid.NewGuid(), siteB, "faq", "keycloak-sub-of-the-owner", "site B's own reason", RevokedAt,
            CancellationToken.None);

        var forSiteA = await repository.ListForSiteAsync(siteA, CancellationToken.None);
        var onlyRow = Assert.Single(forSiteA);
        Assert.Equal("calendar", onlyRow.ModuleKey);
    }
}
