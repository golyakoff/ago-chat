using Ago.Chat.Application.Abstractions;
using Ago.Chat.Application.Tests.Fakes;
using Ago.Chat.Application.UseCases.ListEnabledModulesForSite;
using Ago.Chat.Domain;

namespace Ago.Chat.Application.Tests.UseCases.ListEnabledModulesForSite;

/// <summary>
/// `23-01`: `ModuleEndpoints.HandleGetAsync` used to call `IEnabledModuleReadStore.GetForSiteAsync`
/// directly, with the route's `siteId` compared against nothing - so any authenticated operator of
/// any site could list another tenant's enabled modules by naming its `siteId`. These tests are the
/// handler-level half of the fix: the permission check that endpoint never had.
/// </summary>
public class ListEnabledModulesForSiteHandlerTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly SiteId SiteId = new(Guid.NewGuid());
    private static readonly OperatorId OperatorId = new(Guid.NewGuid());

    private static (ListEnabledModulesForSiteHandler Handler, FakeEnabledModuleReadStore ReadStore) CreateFixture(
        bool grantPermission = true)
    {
        var readStore = new FakeEnabledModuleReadStore();
        var permissions = new FakePermissionChecker();
        if (grantPermission)
        {
            permissions.Grant(OperatorId, SiteId, Permission.SiteConfigure);
        }

        return (new ListEnabledModulesForSiteHandler(readStore, permissions, new FakeClock(Now)), readStore);
    }

    [Fact]
    public async Task HandleAsync_WhenPermitted_ReturnsTheSitesEnabledModules()
    {
        var (handler, readStore) = CreateFixture();
        readStore.Seed(SiteId, new EnabledModuleSummary(
            new ModuleKey("faq"), ["/faq"], new Uri("https://faq.example.com"),
            new ModuleCredential("a-shared-secret-of-sixteen-plus-chars"), GrantedByOwner: false, ExpiresAt: null));

        var result = await handler.HandleAsync(
            new Application.UseCases.ListEnabledModulesForSite.ListEnabledModulesForSite(OperatorId, SiteId),
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        var module = Assert.Single(result.Value);
        Assert.Equal(new ModuleKey("faq"), module.ModuleKey);
    }

    // `23-01`'s own fix: an operator holding no permission on this site - the shape a caller naming
    // another tenant's siteId is in - is refused rather than handed the list.
    [Fact]
    public async Task HandleAsync_WhenTheOperatorLacksSiteConfigure_ReturnsForbidden()
    {
        var (handler, readStore) = CreateFixture(grantPermission: false);
        readStore.Seed(SiteId, new EnabledModuleSummary(
            new ModuleKey("faq"), ["/faq"], new Uri("https://faq.example.com"),
            new ModuleCredential("a-shared-secret-of-sixteen-plus-chars"), GrantedByOwner: false, ExpiresAt: null));

        var result = await handler.HandleAsync(
            new Application.UseCases.ListEnabledModulesForSite.ListEnabledModulesForSite(OperatorId, SiteId),
            CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Conversation.Forbidden", result.Error!.Value.Code);
    }

    [Fact]
    public async Task HandleAsync_WhenTheSiteHasNoModulesEnabled_ReturnsAnEmptyList()
    {
        var (handler, _) = CreateFixture();

        var result = await handler.HandleAsync(
            new Application.UseCases.ListEnabledModulesForSite.ListEnabledModulesForSite(OperatorId, SiteId),
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Empty(result.Value);
    }
}
