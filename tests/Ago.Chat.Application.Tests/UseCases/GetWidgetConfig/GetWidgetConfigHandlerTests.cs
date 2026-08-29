using Ago.Chat.Application.Tests.Fakes;
using Ago.Chat.Application.UseCases.GetWidgetConfig;
using Ago.Chat.Domain;

namespace Ago.Chat.Application.Tests.UseCases.GetWidgetConfig;

public class GetWidgetConfigHandlerTests
{
    private static readonly SiteId SiteId = new(Guid.NewGuid());
    private static readonly OperatorId OperatorId = new(Guid.NewGuid());

    [Fact]
    public async Task HandleAsync_WhenPermitted_ReturnsTheSitesCurrentWidgetConfig()
    {
        var sites = new FakeSiteRepository();
        var permissions = new FakePermissionChecker();
        permissions.Grant(OperatorId, SiteId, Permission.SiteConfigure);
        var site = new Site(SiteId, "shop_7f3a", []);
        site.UpdateWidgetConfig(
            new WidgetConfig("#112233", Position.BottomLeft, "We read what you send us.", "https://tenant.example/privacy"),
            DateTimeOffset.UtcNow);
        site.UpdateLocale(Locale.Ru, DateTimeOffset.UtcNow);
        site.ClearDomainEvents();
        sites.Seed(site);
        var handler = new GetWidgetConfigHandler(sites, permissions);

        var result = await handler.HandleAsync(
            new Application.UseCases.GetWidgetConfig.GetWidgetConfig(SiteId, OperatorId), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal("#112233", result.Value.PrimaryColorHex);
        Assert.Equal(Position.BottomLeft, result.Value.Position);
        Assert.Equal(Locale.Ru, result.Value.Locale);
        Assert.Equal("We read what you send us.", result.Value.NoticeText);
        Assert.Equal("https://tenant.example/privacy", result.Value.NoticeUrl);
    }

    // `16-04`: every site that predates this item, or has simply never set a notice, reads back
    // `null` for both fields - the widget then renders nothing (WidgetConfig's own remarks on why an
    // AGO-authored default would be wrong here).
    [Fact]
    public async Task HandleAsync_WhenNoticeWasNeverSet_ReturnsNullForBothFields()
    {
        var sites = new FakeSiteRepository();
        var permissions = new FakePermissionChecker();
        permissions.Grant(OperatorId, SiteId, Permission.SiteConfigure);
        sites.Seed(new Site(SiteId, "shop_7f3a", []));
        var handler = new GetWidgetConfigHandler(sites, permissions);

        var result = await handler.HandleAsync(
            new Application.UseCases.GetWidgetConfig.GetWidgetConfig(SiteId, OperatorId), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Null(result.Value.NoticeText);
        Assert.Null(result.Value.NoticeUrl);
    }

    // `11-10`: a site that never called `Site.UpdateLocale` - every existing tenant today - reads
    // back `Locale.En` from this handler, matching `Site`'s own default (`Locale`'s own remarks on
    // why `En` is the safe default rather than an arbitrary one).
    [Fact]
    public async Task HandleAsync_WhenLocaleWasNeverSet_ReturnsEnglishDefault()
    {
        var sites = new FakeSiteRepository();
        var permissions = new FakePermissionChecker();
        permissions.Grant(OperatorId, SiteId, Permission.SiteConfigure);
        sites.Seed(new Site(SiteId, "shop_7f3a", []));
        var handler = new GetWidgetConfigHandler(sites, permissions);

        var result = await handler.HandleAsync(
            new Application.UseCases.GetWidgetConfig.GetWidgetConfig(SiteId, OperatorId), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(Locale.En, result.Value.Locale);
    }

    [Fact]
    public async Task HandleAsync_WhenTheOperatorLacksSiteConfigure_ReturnsForbidden()
    {
        var sites = new FakeSiteRepository();
        sites.Seed(new Site(SiteId, "shop_7f3a", []));
        var handler = new GetWidgetConfigHandler(sites, new FakePermissionChecker());

        var result = await handler.HandleAsync(
            new Application.UseCases.GetWidgetConfig.GetWidgetConfig(SiteId, OperatorId), CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Conversation.Forbidden", result.Error!.Value.Code);
    }

    [Fact]
    public async Task HandleAsync_WhenTheSiteDoesNotExist_ReturnsSiteNotFound()
    {
        var sites = new FakeSiteRepository();
        var permissions = new FakePermissionChecker();
        permissions.Grant(OperatorId, SiteId, Permission.SiteConfigure);
        var handler = new GetWidgetConfigHandler(sites, permissions);

        var result = await handler.HandleAsync(
            new Application.UseCases.GetWidgetConfig.GetWidgetConfig(SiteId, OperatorId), CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Site.NotFound", result.Error!.Value.Code);
    }
}
