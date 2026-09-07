using Ago.Chat.Application.Tests.Fakes;
using Ago.Chat.Application.UseCases.ListMyTenancies;
using Ago.Chat.Domain;

namespace Ago.Chat.Application.Tests.UseCases.ListMyTenancies;

/// <summary>`13-07`/`adr/0068`: the console switcher's own read - every `Site` an identity
/// administers, joined to its name, ordered by name.</summary>
public class ListMyTenanciesHandlerTests
{
    [Fact]
    public async Task HandleAsync_WhenTheIdentityHasNoOperatorRow_ReturnsAnEmptyList()
    {
        var handler = new ListMyTenanciesHandler(new FakeOperatorRepository(), new FakeSiteRepository(), new FakePermissionChecker());

        var result = await handler.HandleAsync(new ListMyTenanciesQuery("nobody"), CancellationToken.None);

        Assert.Empty(result);
    }

    [Fact]
    public async Task HandleAsync_WhenTheIdentityAdministersSeveralSites_ReturnsEachOneWithItsName_OrderedByName()
    {
        var operators = new FakeOperatorRepository();
        var sites = new FakeSiteRepository();

        var siteZebra = new SiteId(Guid.NewGuid());
        var siteAcme = new SiteId(Guid.NewGuid());
        sites.Seed(new Site(siteZebra, "site_zebra", [], "Zebra Shop"));
        sites.Seed(new Site(siteAcme, "site_acme", [], "Acme Support"));
        operators.Seed(new Operator(new OperatorId(Guid.NewGuid()), siteZebra, OperatorStatus.Online, 5, "multi-sub"));
        operators.Seed(new Operator(new OperatorId(Guid.NewGuid()), siteAcme, OperatorStatus.Online, 5, "multi-sub"));

        var handler = new ListMyTenanciesHandler(operators, sites, new FakePermissionChecker());

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
        operators.Seed(new Operator(new OperatorId(Guid.NewGuid()), otherSiteId, OperatorStatus.Online, 5, "someone-else"));

        var handler = new ListMyTenanciesHandler(operators, sites, new FakePermissionChecker());

        var result = await handler.HandleAsync(new ListMyTenanciesQuery("me"), CancellationToken.None);

        Assert.Empty(result);
    }

    /// <summary>`23-71`: a seatless administrator's own tenancy is a switchable one - the switcher list
    /// must offer nothing `ResolveOperatorIdentityHandler` could not actually resolve the caller's
    /// token into, and it now can for a `site:manage_operators` holder with no seat.</summary>
    [Fact]
    public async Task HandleAsync_WhenTheIdentityHoldsNoSeatButManagesOperators_StillListsThatTenancy()
    {
        var operators = new FakeOperatorRepository();
        var sites = new FakeSiteRepository();
        var permissions = new FakePermissionChecker();

        var siteId = new SiteId(Guid.NewGuid());
        var operatorId = new OperatorId(Guid.NewGuid());
        sites.Seed(new Site(siteId, "site_admin", [], "Admin's Shop"));
        operators.Seed(new Operator(
            operatorId, siteId, OperatorStatus.Offline, 5, "seatless-admin-sub", holdsSeat: false));
        permissions.Grant(operatorId, siteId, Permission.SiteManageOperators);

        var handler = new ListMyTenanciesHandler(operators, sites, permissions);

        var result = await handler.HandleAsync(new ListMyTenanciesQuery("seatless-admin-sub"), CancellationToken.None);

        var tenancy = Assert.Single(result);
        Assert.Equal(siteId.Value, tenancy.SiteId);
    }

    /// <summary>The complementary case - an ordinary seatless operator (no `site:manage_operators`
    /// grant) is not offered a tenancy their own token cannot actually resolve into.</summary>
    [Fact]
    public async Task HandleAsync_WhenTheIdentityHoldsNoSeatAndDoesNotManageOperators_OmitsThatTenancy()
    {
        var operators = new FakeOperatorRepository();
        var sites = new FakeSiteRepository();

        var siteId = new SiteId(Guid.NewGuid());
        sites.Seed(new Site(siteId, "site_ordinary", [], "Ordinary Shop"));
        operators.Seed(new Operator(
            new OperatorId(Guid.NewGuid()), siteId, OperatorStatus.Offline, 5, "seatless-ordinary-sub", holdsSeat: false));

        var handler = new ListMyTenanciesHandler(operators, sites, new FakePermissionChecker());

        var result = await handler.HandleAsync(new ListMyTenanciesQuery("seatless-ordinary-sub"), CancellationToken.None);

        Assert.Empty(result);
    }
}
