using Ago.Chat.Application.Tests.Fakes;
using Ago.Chat.Application.UseCases.ListMyTenancies;
using Ago.Chat.Domain;

namespace Ago.Chat.Application.Tests.UseCases.ListMyTenancies;

/// <summary>`13-07`/`adr/0068`: the console switcher's own read - every `Site` an identity
/// administers, joined to its name, ordered by name.
///
/// <para><b>`25-170`: <see cref="FakeOperatorRoleRepository"/> replaces <see cref="FakePermissionChecker"/>
/// as this handler's third dependency</b> - see <see cref="ResolveOperatorIdentity.ResolveOperatorIdentityHandlerTests"/>'s
/// own identical remarks for why every listed tenancy now needs its own seeded seat.</para>
/// </summary>
public class ListMyTenanciesHandlerTests
{
    [Fact]
    public async Task HandleAsync_WhenTheIdentityHasNoOperatorRow_ReturnsAnEmptyList()
    {
        var handler = new ListMyTenanciesHandler(new FakeOperatorRepository(), new FakeSiteRepository(), new FakeOperatorRoleRepository());

        var result = await handler.HandleAsync(new ListMyTenanciesQuery("nobody"), CancellationToken.None);

        Assert.Empty(result);
    }

    [Fact]
    public async Task HandleAsync_WhenTheIdentityAdministersSeveralSites_ReturnsEachOneWithItsName_OrderedByName()
    {
        var operators = new FakeOperatorRepository();
        var sites = new FakeSiteRepository();
        var operatorRoles = new FakeOperatorRoleRepository();

        var siteZebra = new SiteId(Guid.NewGuid());
        var siteAcme = new SiteId(Guid.NewGuid());
        sites.Seed(new Site(siteZebra, "site_zebra", [], "Zebra Shop"));
        sites.Seed(new Site(siteAcme, "site_acme", [], "Acme Support"));
        var operatorZebra = new OperatorId(Guid.NewGuid());
        var operatorAcme = new OperatorId(Guid.NewGuid());
        operators.Seed(new Operator(operatorZebra, siteZebra, OperatorStatus.Online, 5, "multi-sub"));
        operators.Seed(new Operator(operatorAcme, siteAcme, OperatorStatus.Online, 5, "multi-sub"));
        operatorRoles.SeedSeat(operatorZebra, "Operator", holdsSeat: true);
        operatorRoles.SeedSeat(operatorAcme, "Operator", holdsSeat: true);

        var handler = new ListMyTenanciesHandler(operators, sites, operatorRoles);

        var result = await handler.HandleAsync(new ListMyTenanciesQuery("multi-sub"), CancellationToken.None);

        Assert.Equal(2, result.Count);
        Assert.Equal("Acme Support", result[0].SiteName);
        Assert.Equal(siteAcme.Value, result[0].SiteId);
        Assert.Equal("Zebra Shop", result[1].SiteName);
        Assert.Equal(siteZebra.Value, result[1].SiteId);
    }

    [Fact]
    public async Task HandleAsync_NeverReturnsATenancyBelongingToADifferentIdentity()
    {
        var operators = new FakeOperatorRepository();
        var sites = new FakeSiteRepository();
        var otherSiteId = new SiteId(Guid.NewGuid());
        sites.Seed(new Site(otherSiteId, "site_other", [], "Someone Else's Shop"));
        var otherOperatorId = new OperatorId(Guid.NewGuid());
        operators.Seed(new Operator(otherOperatorId, otherSiteId, OperatorStatus.Online, 5, "someone-else"));
        var operatorRoles = new FakeOperatorRoleRepository();
        operatorRoles.SeedSeat(otherOperatorId, "Operator", holdsSeat: true);

        var handler = new ListMyTenanciesHandler(operators, sites, operatorRoles);

        var result = await handler.HandleAsync(new ListMyTenanciesQuery("me"), CancellationToken.None);

        Assert.Empty(result);
    }

    /// <summary>`23-71`/`25-170`: a seatless-on-the-Operator-role administrator's own tenancy is still a
    /// switchable one, so long as the Admin role's own seat is held - the switcher list must offer
    /// nothing `ResolveOperatorIdentityHandler` could not actually resolve the caller's token into.</summary>
    [Fact]
    public async Task HandleAsync_WhenTheIdentitysAdminRoleHoldsASeat_StillListsThatTenancy()
    {
        var operators = new FakeOperatorRepository();
        var sites = new FakeSiteRepository();
        var operatorRoles = new FakeOperatorRoleRepository();

        var siteId = new SiteId(Guid.NewGuid());
        var operatorId = new OperatorId(Guid.NewGuid());
        sites.Seed(new Site(siteId, "site_admin", [], "Admin's Shop"));
        operators.Seed(new Operator(operatorId, siteId, OperatorStatus.Offline, 5, "seatless-operator-role-admin-sub"));
        operatorRoles.SeedSeat(operatorId, "Operator", holdsSeat: false);
        operatorRoles.SeedSeat(operatorId, "Admin", holdsSeat: true);

        var handler = new ListMyTenanciesHandler(operators, sites, operatorRoles);

        var result = await handler.HandleAsync(new ListMyTenanciesQuery("seatless-operator-role-admin-sub"), CancellationToken.None);

        var tenancy = Assert.Single(result);
        Assert.Equal(siteId.Value, tenancy.SiteId);
    }

    /// <summary>The complementary case - an operator seatless on every role it holds is not offered a
    /// tenancy their own token cannot actually resolve into.</summary>
    [Fact]
    public async Task HandleAsync_WhenTheIdentityHoldsNoSeatOnAnyRole_OmitsThatTenancy()
    {
        var operators = new FakeOperatorRepository();
        var sites = new FakeSiteRepository();
        var operatorRoles = new FakeOperatorRoleRepository();

        var siteId = new SiteId(Guid.NewGuid());
        var operatorId = new OperatorId(Guid.NewGuid());
        sites.Seed(new Site(siteId, "site_ordinary", [], "Ordinary Shop"));
        operators.Seed(new Operator(operatorId, siteId, OperatorStatus.Offline, 5, "seatless-ordinary-sub"));
        operatorRoles.SeedSeat(operatorId, "Operator", holdsSeat: false);

        var handler = new ListMyTenanciesHandler(operators, sites, operatorRoles);

        var result = await handler.HandleAsync(new ListMyTenanciesQuery("seatless-ordinary-sub"), CancellationToken.None);

        Assert.Empty(result);
    }
}
