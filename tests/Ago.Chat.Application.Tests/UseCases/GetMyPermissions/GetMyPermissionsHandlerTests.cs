using Ago.Chat.Application.Abstractions;
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

    private static GetMyPermissionsHandler HandlerFor(
        Site site,
        FakePermissionChecker? permissions = null,
        FakeEnabledModuleReadStore? modules = null,
        FakeOperatorRepository? operators = null) =>
        new(
            permissions ?? new FakePermissionChecker(),
            SiteConfigFor(site),
            modules ?? new FakeEnabledModuleReadStore(),
            new FakeClock(DateTimeOffset.UtcNow),
            operators ?? new FakeOperatorRepository());

    [Fact]
    public async Task HandleAsync_ReturnsEveryPermissionTheOperatorsRolesGrantForThatSite()
    {
        var siteId = new SiteId(Guid.NewGuid());
        var operatorId = new OperatorId(Guid.NewGuid());
        var permissions = new FakePermissionChecker();
        permissions.Grant(operatorId, siteId, Permission.ConversationRead);
        permissions.Grant(operatorId, siteId, Permission.AttachmentDelete);
        var handler = HandlerFor(new Site(siteId, "shop_test", []), permissions);

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
        var handler = HandlerFor(new Site(siteId, "shop_test", []));

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
        var handler = HandlerFor(site);

        var result = await handler.HandleAsync(new Application.UseCases.GetMyPermissions.GetMyPermissions(operatorId, siteId), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal("Ru", result.Value.Locale);
    }

    [Fact]
    public async Task HandleAsync_WhenNoLocaleWasEverSet_DefaultsToEn()
    {
        var siteId = new SiteId(Guid.NewGuid());
        var operatorId = new OperatorId(Guid.NewGuid());
        var handler = HandlerFor(new Site(siteId, "shop_test", []));

        var result = await handler.HandleAsync(new Application.UseCases.GetMyPermissions.GetMyPermissions(operatorId, siteId), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal("En", result.Value.Locale);
    }

    [Fact]
    public async Task HandleAsync_ReturnsTheCallersOwnSitesEnabledModules()
    {
        // `23-21`: the "what does this tenant have at all" half of the response, kept separate from
        // Permissions - see this handler's own remarks for why the two must never be merged into one
        // list.
        var siteId = new SiteId(Guid.NewGuid());
        var operatorId = new OperatorId(Guid.NewGuid());
        var modules = new FakeEnabledModuleReadStore();
        modules.Seed(siteId, new EnabledModuleSummary(
            new ModuleKey("calendar"), ["book"], new Uri("https://module.example/entry"),
            new ModuleCredential("test-module-credential-value"), GrantedByOwner: false, ExpiresAt: null));
        var handler = HandlerFor(new Site(siteId, "shop_test", []), modules: modules);

        var result = await handler.HandleAsync(new Application.UseCases.GetMyPermissions.GetMyPermissions(operatorId, siteId), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Contains("calendar", result.Value.EnabledModules);
    }

    [Fact]
    public async Task HandleAsync_NeverReturnsAnotherSitesEnabledModules()
    {
        // `23-21`'s own scope requirement: reading the tenant half must not become a second
        // uncontrolled cross-tenant read, the exact failure `23-01` closed on the neighbouring route
        // (`ListEnabledModulesForSiteHandler`'s own remarks). This handler is never handed a
        // caller-chosen siteId to begin with - `GetMyPermissions.SiteId` is always the operator claim -
        // so this proves the read itself is scoped, not merely that no route lets a caller choose.
        var callersSite = new SiteId(Guid.NewGuid());
        var anotherTenantsSite = new SiteId(Guid.NewGuid());
        var operatorId = new OperatorId(Guid.NewGuid());
        var modules = new FakeEnabledModuleReadStore();
        modules.Seed(anotherTenantsSite, new EnabledModuleSummary(
            new ModuleKey("calendar"), ["book"], new Uri("https://module.example/entry"),
            new ModuleCredential("test-module-credential-value"), GrantedByOwner: false, ExpiresAt: null));
        var handler = HandlerFor(new Site(callersSite, "shop_test", []), modules: modules);

        var result = await handler.HandleAsync(
            new Application.UseCases.GetMyPermissions.GetMyPermissions(operatorId, callersSite), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Empty(result.Value.EnabledModules);
    }

    /// <summary>`23-02`: `decisions.md` §1's "rewritten at every sign-in" - this call is where it
    /// happens, so the response it returns must carry the value it just wrote, not a stale one.</summary>
    [Fact]
    public async Task HandleAsync_RefreshesTheOperatorsIdentity_AndReturnsTheNameFromTheCall()
    {
        var siteId = new SiteId(Guid.NewGuid());
        var operatorId = new OperatorId(Guid.NewGuid());
        var operators = new FakeOperatorRepository();
        operators.Seed(new Operator(operatorId, siteId, OperatorStatus.Online, capacity: 5));
        var handler = HandlerFor(new Site(siteId, "shop_test", []), operators: operators);

        var result = await handler.HandleAsync(
            new Application.UseCases.GetMyPermissions.GetMyPermissions(operatorId, siteId, "Ivan Petrov", "ivan@example.test"),
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal("Ivan Petrov", result.Value.DisplayName);
        Assert.Equal(("Ivan Petrov", "ivan@example.test"), operators.CurrentIdentity(operatorId));
    }

    /// <summary>A caller that supplies no name/email (a token minted without them, or a test that does
    /// not care) must not crash the response - `DisplayName` is simply absent.</summary>
    [Fact]
    public async Task HandleAsync_WithNoNameClaim_ReturnsANullDisplayName()
    {
        var siteId = new SiteId(Guid.NewGuid());
        var operatorId = new OperatorId(Guid.NewGuid());
        var handler = HandlerFor(new Site(siteId, "shop_test", []));

        var result = await handler.HandleAsync(
            new Application.UseCases.GetMyPermissions.GetMyPermissions(operatorId, siteId), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Null(result.Value.DisplayName);
    }
}
