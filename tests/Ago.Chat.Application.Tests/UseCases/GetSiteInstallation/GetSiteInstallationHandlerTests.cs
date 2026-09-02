using Ago.Chat.Application.Tests.Fakes;
using Ago.Chat.Application.UseCases.GetSiteInstallation;
using Ago.Chat.Domain;

namespace Ago.Chat.Application.Tests.UseCases.GetSiteInstallation;

/// <summary>
/// `10-06`. Modeled on `GetWidgetConfigHandlerTests` - the identical shape (permitted/forbidden/not
/// found) for the identical gate (`Permission.SiteConfigure`, reused rather than a new permission).
/// The property that actually matters here - one site's operator cannot read a *different* site's
/// key - is proven over real HTTP in `CrossTenantRouteIsolationTests`
/// (`Ago.Chat.Integration.Tests`), not here: a handler test with a fake permission checker can only
/// prove "when the checker says no, the handler refuses", which is a weaker claim than "an operator
/// of another tenant is refused" (that file's own doc comment has the full reasoning). What belongs
/// here is the ordinary shape of this one handler in isolation.
/// </summary>
public class GetSiteInstallationHandlerTests
{
    private static readonly SiteId SiteId = new(Guid.NewGuid());
    private static readonly OperatorId OperatorId = new(Guid.NewGuid());

    [Fact]
    public async Task HandleAsync_WhenPermitted_ReturnsTheSitesPublicKeyAndAllowedOrigins()
    {
        var sites = new FakeSiteRepository();
        var permissions = new FakePermissionChecker();
        permissions.Grant(OperatorId, SiteId, Permission.SiteConfigure);
        sites.Seed(new Site(SiteId, "shop_7f3a", ["https://tenant.example"]));
        var handler = new GetSiteInstallationHandler(sites, permissions);

        var result = await handler.HandleAsync(
            new Application.UseCases.GetSiteInstallation.GetSiteInstallation(SiteId, OperatorId), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal("shop_7f3a", result.Value.PublicKey);
        Assert.Equal(["https://tenant.example"], result.Value.AllowedOrigins);
    }

    [Fact]
    public async Task HandleAsync_WhenTheOperatorLacksSiteConfigure_ReturnsForbidden()
    {
        var sites = new FakeSiteRepository();
        sites.Seed(new Site(SiteId, "shop_7f3a", ["https://tenant.example"]));
        var handler = new GetSiteInstallationHandler(sites, new FakePermissionChecker());

        var result = await handler.HandleAsync(
            new Application.UseCases.GetSiteInstallation.GetSiteInstallation(SiteId, OperatorId), CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Conversation.Forbidden", result.Error!.Value.Code);
    }

    [Fact]
    public async Task HandleAsync_WhenTheSiteDoesNotExist_ReturnsSiteNotFound()
    {
        var sites = new FakeSiteRepository();
        var permissions = new FakePermissionChecker();
        permissions.Grant(OperatorId, SiteId, Permission.SiteConfigure);
        var handler = new GetSiteInstallationHandler(sites, permissions);

        var result = await handler.HandleAsync(
            new Application.UseCases.GetSiteInstallation.GetSiteInstallation(SiteId, OperatorId), CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Site.NotFound", result.Error!.Value.Code);
    }
}
