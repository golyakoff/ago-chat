using Ago.Chat.Application.Tests.Fakes;
using Ago.Chat.Application.UseCases.GetSiteBranding;
using Ago.Chat.Domain;

namespace Ago.Chat.Application.Tests.UseCases.GetSiteBranding;

public class GetSiteBrandingHandlerTests
{
    private static readonly SiteId SiteId = new(Guid.NewGuid());
    private static readonly OperatorId OperatorId = new(Guid.NewGuid());
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task HandleAsync_WithNoLogo_ReturnsANullLogoUrl()
    {
        var sites = new FakeSiteRepository();
        sites.Seed(new Site(SiteId, "shop_7f3a", []));
        var permissions = new FakePermissionChecker();
        permissions.Grant(OperatorId, SiteId, Permission.SiteConfigure);
        var handler = new GetSiteBrandingHandler(sites, permissions, new FakeSiteLogoPublicUrlBuilder());

        var result = await handler.HandleAsync(new Application.UseCases.GetSiteBranding.GetSiteBranding(SiteId, OperatorId), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Null(result.Value.LogoUrl);
        Assert.Equal(LogoStatus.None, result.Value.LogoStatus);
    }

    [Fact]
    public async Task HandleAsync_WithAReadyLogo_ReturnsItsPublicUrl()
    {
        var site = new Site(SiteId, "shop_7f3a", []);
        const string pendingKey = "site/pending/test.png";
        const string publicKey = "site/logo/test.png";
        site.SubmitLogoUpload(pendingKey, "image/png", Now);
        site.PromoteLogo(pendingKey, publicKey, Now);
        site.ClearDomainEvents();
        var sites = new FakeSiteRepository();
        sites.Seed(site);
        var permissions = new FakePermissionChecker();
        permissions.Grant(OperatorId, SiteId, Permission.SiteConfigure);
        var handler = new GetSiteBrandingHandler(sites, permissions, new FakeSiteLogoPublicUrlBuilder());

        var result = await handler.HandleAsync(new Application.UseCases.GetSiteBranding.GetSiteBranding(SiteId, OperatorId), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal($"https://files.test/attachments/{publicKey}", result.Value.LogoUrl);
        Assert.Equal(LogoStatus.Ready, result.Value.LogoStatus);
    }

    [Fact]
    public async Task HandleAsync_WhenNotPermitted_ReturnsForbidden()
    {
        var sites = new FakeSiteRepository();
        sites.Seed(new Site(SiteId, "shop_7f3a", []));
        var handler = new GetSiteBrandingHandler(sites, new FakePermissionChecker(), new FakeSiteLogoPublicUrlBuilder());

        var result = await handler.HandleAsync(new Application.UseCases.GetSiteBranding.GetSiteBranding(SiteId, OperatorId), CancellationToken.None);

        Assert.True(result.IsFailure);
    }
}
