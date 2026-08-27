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
        var handler = new ListMyTenanciesHandler(new FakeOperatorRepository(), new FakeSiteRepository());

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

        var handler = new ListMyTenanciesHandler(operators, sites);

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

        var handler = new ListMyTenanciesHandler(operators, sites);

        var result = await handler.HandleAsync(new ListMyTenanciesQuery("me"), CancellationToken.None);

        Assert.Empty(result);
    }
}
