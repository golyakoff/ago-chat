using Ago.Chat.Domain;
using Ago.Chat.Infrastructure.Postgres;

namespace Ago.Chat.Integration.Tests;

/// <summary>
/// `23-68`: proves the one guarantee no fake can stand in for (`testing.md`) - a real round trip
/// against Postgres, with no `sites`/`operators` foreign key to violate
/// (`OperatorSeatRestoreOverrideEntityConfiguration`'s own remarks), and rows for two different sites
/// never bleed into one tenant's own list. The identical shape
/// <see cref="ModuleRevokeOverrideRepositoryTests"/> already proves for its sibling table.
/// </summary>
[Collection(PostgresCollection.Name)]
public class OperatorSeatRestoreOverrideRepositoryTests(PostgresFixture fixture)
{
    private static readonly DateTimeOffset RestoredAt = new(2026, 9, 8, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task RecordAsync_ThenListForSiteAsync_RoundTripsTheRow()
    {
        var siteId = new SiteId(Guid.NewGuid());
        var operatorId = new OperatorId(Guid.NewGuid());
        var repository = new OperatorSeatRestoreOverrideRepository(fixture.DataSource);

        await repository.RecordAsync(
            Guid.NewGuid(), siteId, operatorId, "keycloak-sub-of-the-owner",
            "Tenant locked itself out during a live demo; restoring the sole operator's seat.", RestoredAt,
            CancellationToken.None);

        var recorded = Assert.Single(await repository.ListForSiteAsync(siteId, CancellationToken.None));
        Assert.Equal(siteId, recorded.SiteId);
        Assert.Equal(operatorId, recorded.OperatorId);
        Assert.Equal("keycloak-sub-of-the-owner", recorded.RestoredBy);
        Assert.Equal("Tenant locked itself out during a live demo; restoring the sole operator's seat.", recorded.Reason);
        Assert.Equal(RestoredAt, recorded.RestoredAt);
    }

    /// <summary>No foreign key to `sites` or `operators` - a row can be written for ids that were
    /// never registered at all, the same deliberate absence `module_revoke_overrides`/`access_records`
    /// already establish for the identical "must survive the tenant's own eventual erasure" reason.</summary>
    [Fact]
    public async Task RecordAsync_ForASiteAndOperatorWithNoRowAtAll_StillSucceeds()
    {
        var neverRegisteredSiteId = new SiteId(Guid.NewGuid());
        var neverRegisteredOperatorId = new OperatorId(Guid.NewGuid());
        var repository = new OperatorSeatRestoreOverrideRepository(fixture.DataSource);

        await repository.RecordAsync(
            Guid.NewGuid(), neverRegisteredSiteId, neverRegisteredOperatorId, "keycloak-sub-of-the-owner",
            "a reason", RestoredAt, CancellationToken.None);

        Assert.Single(await repository.ListForSiteAsync(neverRegisteredSiteId, CancellationToken.None));
    }

    [Fact]
    public async Task ListForSiteAsync_NeverReturnsAnotherSitesOwnOverrides()
    {
        var siteA = new SiteId(Guid.NewGuid());
        var siteB = new SiteId(Guid.NewGuid());
        var operatorA = new OperatorId(Guid.NewGuid());
        var operatorB = new OperatorId(Guid.NewGuid());
        var repository = new OperatorSeatRestoreOverrideRepository(fixture.DataSource);

        await repository.RecordAsync(
            Guid.NewGuid(), siteA, operatorA, "keycloak-sub-of-the-owner", "site A's own reason", RestoredAt,
            CancellationToken.None);
        await repository.RecordAsync(
            Guid.NewGuid(), siteB, operatorB, "keycloak-sub-of-the-owner", "site B's own reason", RestoredAt,
            CancellationToken.None);

        var forSiteA = await repository.ListForSiteAsync(siteA, CancellationToken.None);
        var onlyRow = Assert.Single(forSiteA);
        Assert.Equal(operatorA, onlyRow.OperatorId);
    }
}
