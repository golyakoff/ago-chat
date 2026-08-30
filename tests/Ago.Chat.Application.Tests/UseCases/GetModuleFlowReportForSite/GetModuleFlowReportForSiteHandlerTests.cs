using Ago.Chat.Application.Tests.Fakes;
using Ago.Chat.Application.UseCases.GetModuleFlowReportForSite;
using Ago.Chat.Domain;

namespace Ago.Chat.Application.Tests.UseCases.GetModuleFlowReportForSite;

public class GetModuleFlowReportForSiteHandlerTests
{
    private static readonly SiteId SiteId = new(Guid.NewGuid());
    private static readonly SiteId OtherSiteId = new(Guid.NewGuid());
    private static readonly OperatorId AdminId = new(Guid.NewGuid());
    private static readonly DateTimeOffset Now = new(2026, 6, 15, 12, 0, 0, TimeSpan.Zero);

    // `18-14`'s own fails-before proof for the module-key wiring: a real, valid module key, resolved
    // from options rather than a literal - see ModuleFlowReportOptions' own remarks for why this
    // string cannot live in Ago.Chat.Application's own source instead. Test code is outside the
    // Roslyn guard's scan root (`src/`), so a literal here is fine, matching every other handler test
    // that seeds a real ModuleKey by hand (e.g. RouteConversationToModuleHandlerTests).
    private const string ConfiguredModuleKey = "site-booking-flow";

    private static (GetModuleFlowReportForSiteHandler Handler, FakeModuleFlowReadStore Store) CreateFixture(
        bool grantPermission = true)
    {
        var store = new FakeModuleFlowReadStore();
        var permissions = new FakePermissionChecker();
        if (grantPermission)
        {
            permissions.Grant(AdminId, SiteId, Permission.SiteConfigure);
        }

        var clock = new FakeClock(Now);
        var options = new ModuleFlowReportOptions { ModuleKey = ConfiguredModuleKey };
        return (new GetModuleFlowReportForSiteHandler(store, permissions, clock, options), store);
    }

    [Fact]
    public async Task HandleAsync_WithoutSiteConfigure_ReturnsForbidden()
    {
        var (handler, _) = CreateFixture(grantPermission: false);

        var result = await handler.HandleAsync(
            new Application.UseCases.GetModuleFlowReportForSite.GetModuleFlowReportForSite(AdminId, SiteId, null, null),
            CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Conversation.Forbidden", result.Error!.Value.Code);
    }

    [Fact]
    public async Task HandleAsync_WhenFromIsNotBeforeTo_ReturnsModuleFlowInvalidRange()
    {
        var (handler, _) = CreateFixture();

        var result = await handler.HandleAsync(
            new Application.UseCases.GetModuleFlowReportForSite.GetModuleFlowReportForSite(
                AdminId, SiteId, Now, Now.AddDays(-1)),
            CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("ModuleFlow.InvalidRange", result.Error!.Value.Code);
    }

    [Fact]
    public async Task HandleAsync_WhenFromEqualsTo_ReturnsModuleFlowInvalidRange()
    {
        var (handler, _) = CreateFixture();

        var result = await handler.HandleAsync(
            new Application.UseCases.GetModuleFlowReportForSite.GetModuleFlowReportForSite(AdminId, SiteId, Now, Now),
            CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("ModuleFlow.InvalidRange", result.Error!.Value.Code);
    }

    /// <summary>The same bound decision `18-08`'s own handler makes: naming no range does not reject
    /// the report, it defaults one, and the response always echoes back exactly what was reported on.
    /// </summary>
    [Fact]
    public async Task HandleAsync_WhenNoRangeIsSupplied_DefaultsToTheTrailingWindow_AndEchoesItBack()
    {
        var (handler, store) = CreateFixture();

        var result = await handler.HandleAsync(
            new Application.UseCases.GetModuleFlowReportForSite.GetModuleFlowReportForSite(AdminId, SiteId, null, null),
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        var expectedFrom = Now.AddDays(-GetModuleFlowReportForSiteHandler.DefaultWindowDays);
        Assert.Equal(expectedFrom, result.Value.From);
        Assert.Equal(Now, result.Value.To);
        Assert.Equal(expectedFrom, store.LastFrom);
        Assert.Equal(Now, store.LastTo);
    }

    [Fact]
    public async Task HandleAsync_WhenARangeIsSupplied_PassesItThroughUnchanged()
    {
        var (handler, store) = CreateFixture();
        var from = Now.AddDays(-10);
        var to = Now.AddDays(-1);

        var result = await handler.HandleAsync(
            new Application.UseCases.GetModuleFlowReportForSite.GetModuleFlowReportForSite(AdminId, SiteId, from, to),
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(from, result.Value.From);
        Assert.Equal(to, result.Value.To);
        Assert.Equal(from, store.LastFrom);
        Assert.Equal(to, store.LastTo);
    }

    [Fact]
    public async Task HandleAsync_PassesTheCallersOwnSiteId_NeverAnother()
    {
        var (handler, store) = CreateFixture();

        await handler.HandleAsync(
            new Application.UseCases.GetModuleFlowReportForSite.GetModuleFlowReportForSite(AdminId, SiteId, null, null),
            CancellationToken.None);

        Assert.Equal(SiteId, store.LastSiteId);
        Assert.NotEqual(OtherSiteId, store.LastSiteId);
    }

    /// <summary>The one thing this handler alone is responsible for translating: the configured raw
    /// string becomes a real <see cref="ModuleKey"/> passed to the read store - never the raw string,
    /// and never a value this handler invented.</summary>
    [Fact]
    public async Task HandleAsync_PassesTheConfiguredModuleKey_ToTheReadStore()
    {
        var (handler, store) = CreateFixture();

        await handler.HandleAsync(
            new Application.UseCases.GetModuleFlowReportForSite.GetModuleFlowReportForSite(AdminId, SiteId, null, null),
            CancellationToken.None);

        Assert.Equal(new ModuleKey(ConfiguredModuleKey), store.LastModuleKey);
    }

    [Fact]
    public async Task HandleAsync_MapsFlowsStartedAndFlowsClosed_FromTheStore()
    {
        var (handler, store) = CreateFixture();
        store.Seed(new Application.Abstractions.ModuleFlowReportResult(FlowsStarted: 7, FlowsClosed: 4));

        var result = await handler.HandleAsync(
            new Application.UseCases.GetModuleFlowReportForSite.GetModuleFlowReportForSite(AdminId, SiteId, null, null),
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(7, result.Value.FlowsStarted);
        Assert.Equal(4, result.Value.FlowsClosed);
    }
}
