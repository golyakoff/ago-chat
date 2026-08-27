using Ago.Chat.Application.Tests.Fakes;
using Ago.Chat.Application.UseCases.GetMyPermissions;
using Ago.Chat.Application.UseCases.GetSiteConfigById;
using Ago.Chat.Domain;

namespace Ago.Chat.Application.Tests.UseCases.GetMyPermissions;

public class GetMyPermissionsHandlerTests
{
    private static GetSiteConfigByIdHandler SiteConfigFor(Site site)
    {
        var sites = new FakeSiteRepository();
        sites.Seed(site);
        return new GetSiteConfigByIdHandler(sites, new FakeCache());
    }

    [Fact]
    public async Task HandleAsync_ReturnsEveryPermissionTheOperatorsRolesGrantForThatSite()
    {
        var siteId = new SiteId(Guid.NewGuid());
        var operatorId = new OperatorId(Guid.NewGuid());
        var permissions = new FakePermissionChecker();
        permissions.Grant(operatorId, siteId, Permission.ConversationRead);
        permissions.Grant(operatorId, siteId, Permission.AttachmentDelete);
        var handler = new GetMyPermissionsHandler(permissions, SiteConfigFor(new Site(siteId, "shop_test", [])));

        var result = await handler.HandleAsync(new Application.UseCases.GetMyPermissions.GetMyPermissions(operatorId, siteId), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(operatorId.Value, result.Value.OperatorId);
        Assert.Equal(siteId.Value, result.Value.SiteId);
        Assert.Contains(Permission.ConversationRead.Value, result.Value.Permissions);
        Assert.Contains(Permission.AttachmentDelete.Value, result.Value.Permissions);
    }

    [Fact]
    public async Task HandleAsync_WhenTheOperatorHasNoRoleForThisSite_ReturnsAnEmptyList()
    {
        var siteId = new SiteId(Guid.NewGuid());
        var operatorId = new OperatorId(Guid.NewGuid());
        var handler = new GetMyPermissionsHandler(
            new FakePermissionChecker(), SiteConfigFor(new Site(siteId, "shop_test", [])));

        var result = await handler.HandleAsync(new Application.UseCases.GetMyPermissions.GetMyPermissions(operatorId, siteId), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Empty(result.Value.Permissions);
    }

    [Fact]
    public async Task HandleAsync_ReturnsTheActiveSitesOwnLocale()
    {
        // `11-11`(console): the console's own reason to read this - deciding which language to
        // render its chrome in, the same tenant-level setting `11-10` already made the widget read.
        var siteId = new SiteId(Guid.NewGuid());
        var operatorId = new OperatorId(Guid.NewGuid());
        var site = new Site(siteId, "shop_test", []);
        site.UpdateLocale(Locale.Ru, DateTimeOffset.UtcNow);
        var handler = new GetMyPermissionsHandler(new FakePermissionChecker(), SiteConfigFor(site));

        var result = await handler.HandleAsync(new Application.UseCases.GetMyPermissions.GetMyPermissions(operatorId, siteId), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal("Ru", result.Value.Locale);
    }

    [Fact]
    public async Task HandleAsync_WhenNoLocaleWasEverSet_DefaultsToEn()
    {
        var siteId = new SiteId(Guid.NewGuid());
        var operatorId = new OperatorId(Guid.NewGuid());
        var handler = new GetMyPermissionsHandler(
            new FakePermissionChecker(), SiteConfigFor(new Site(siteId, "shop_test", [])));

        var result = await handler.HandleAsync(new Application.UseCases.GetMyPermissions.GetMyPermissions(operatorId, siteId), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal("En", result.Value.Locale);
    }
}
