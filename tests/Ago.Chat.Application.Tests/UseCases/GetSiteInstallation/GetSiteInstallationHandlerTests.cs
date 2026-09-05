using Ago.Chat.Application.Abstractions;
using Ago.Chat.Application.Tests.Fakes;
using Ago.Chat.Application.UseCases.GetSiteInstallation;
using Ago.Chat.Domain;

namespace Ago.Chat.Application.Tests.UseCases.GetSiteInstallation;

/// <summary>
/// `10-06`. Modeled on `GetWidgetConfigHandlerTests` - the identical shape (permitted/forbidden/not
/// found) for the identical gate (`Permission.SiteConfigure`, reused rather than a new permission). The
/// property that actually matters here - one site's operator cannot read a *different* site's key - is
/// proven over real HTTP in `CrossTenantRouteIsolationTests` (`Ago.Chat.Integration.Tests`), not here: a
/// handler test with a fake permission checker can only prove "when the checker says no, the handler
/// refuses", which is a weaker claim than "an operator of another tenant is refused" (that file's own
/// doc comment has the full reasoning). What belongs here is the ordinary shape of this one handler in
/// isolation.
///
/// <para><b>`23-06`</b>: every test below now needs the two new fake dependencies
/// (<see cref="FakeSiteInstallationSignalRepository"/>, <see cref="FakeConversationReadStore"/>) and a
/// <see cref="FakeClock"/> - <see cref="CreateHandler"/> centralises the wiring so the permitted/
/// forbidden/not-found tests above do not have to restate four constructor arguments they do not care
/// about. The new tests below are this item's own: what the resolved state is for each of the four
/// facts, and that a stale-refusal / later-success ordering is read correctly.</para>
/// </summary>
public class GetSiteInstallationHandlerTests
{
    private static readonly SiteId SiteId = new(Guid.NewGuid());
    private static readonly OperatorId OperatorId = new(Guid.NewGuid());
    private static readonly DateTimeOffset Now = new(2026, 9, 5, 12, 0, 0, TimeSpan.Zero);

    private static GetSiteInstallationHandler CreateHandler(
        FakeSiteRepository sites,
        FakePermissionChecker permissions,
        FakeSiteInstallationSignalRepository? signals = null,
        FakeConversationReadStore? conversations = null,
        int recentlyThresholdDays = 7) =>
        new(
            sites,
            permissions,
            signals ?? new FakeSiteInstallationSignalRepository(),
            conversations ?? new FakeConversationReadStore(),
            new FakeClock(Now),
            new SiteInstallationOptions { RecentlyThresholdDays = recentlyThresholdDays });

    [Fact]
    public async Task HandleAsync_WhenPermitted_ReturnsTheSitesPublicKeyAndAllowedOrigins()
    {
        var sites = new FakeSiteRepository();
        var permissions = new FakePermissionChecker();
        permissions.Grant(OperatorId, SiteId, Permission.SiteConfigure);
        sites.Seed(new Site(SiteId, "shop_7f3a", ["https://tenant.example"]));
        var handler = CreateHandler(sites, permissions);

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
        var handler = CreateHandler(sites, new FakePermissionChecker());

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
        var handler = CreateHandler(sites, permissions);

        var result = await handler.HandleAsync(
            new Application.UseCases.GetSiteInstallation.GetSiteInstallation(SiteId, OperatorId), CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Site.NotFound", result.Error!.Value.Code);
    }

    /// <summary>`23-06`'s own Done-when: "A site never seen and a site seen long ago are
    /// distinguishable through the installation read" - the brand-new-tenant half of that pair.
    /// Nothing has ever been written for this site, so every fact is unset and the resolved state is
    /// <see cref="SiteInstallationState.NotSeenYet"/> - the state a brand-new tenant gets on day
    /// one.</summary>
    [Fact]
    public async Task HandleAsync_WhenNothingHasEverBeenSeen_ResolvesToNotSeenYet()
    {
        var sites = new FakeSiteRepository();
        var permissions = new FakePermissionChecker();
        permissions.Grant(OperatorId, SiteId, Permission.SiteConfigure);
        sites.Seed(new Site(SiteId, "shop_7f3a", ["https://tenant.example"]));
        var handler = CreateHandler(sites, permissions);

        var result = await handler.HandleAsync(
            new Application.UseCases.GetSiteInstallation.GetSiteInstallation(SiteId, OperatorId), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(SiteInstallationState.NotSeenYet, result.Value.State);
        Assert.Null(result.Value.FirstSeenAt);
        Assert.Null(result.Value.LastSeenAt);
        Assert.False(result.Value.UsedRecently);
    }

    /// <summary>The seen-long-ago half of the same Done-when pair - a site with a real
    /// <see cref="SiteInstallationSignals.LastSeenAt"/> resolves to
    /// <see cref="SiteInstallationState.SeenAndQuiet"/>, distinguishable from the brand-new tenant
    /// above purely through what this read returns.</summary>
    [Fact]
    public async Task HandleAsync_WhenTheWidgetHasBeenSeen_ResolvesToSeenAndQuiet()
    {
        var sites = new FakeSiteRepository();
        var permissions = new FakePermissionChecker();
        permissions.Grant(OperatorId, SiteId, Permission.SiteConfigure);
        sites.Seed(new Site(SiteId, "shop_7f3a", ["https://tenant.example"]));
        var signals = new FakeSiteInstallationSignalRepository();
        var firstSeen = Now.AddDays(-30);
        var lastSeen = Now.AddDays(-20);
        signals.Seed(SiteId, new SiteInstallationSignals(firstSeen, lastSeen, null, null));
        var handler = CreateHandler(sites, permissions, signals);

        var result = await handler.HandleAsync(
            new Application.UseCases.GetSiteInstallation.GetSiteInstallation(SiteId, OperatorId), CancellationToken.None);

        Assert.Equal(SiteInstallationState.SeenAndQuiet, result.Value.State);
        Assert.Equal(firstSeen, result.Value.FirstSeenAt);
        Assert.Equal(lastSeen, result.Value.LastSeenAt);
    }

    /// <summary>The classic `www.` vs. bare-domain failure `decisions.md` §3 names: the widget has
    /// never been recorded as seen, but a refusal has - <see cref="SiteInstallationState.EveryRequestRefused"/>,
    /// not <see cref="SiteInstallationState.NotSeenYet"/>.</summary>
    [Fact]
    public async Task HandleAsync_WhenNeverSeenButARefusalIsOnRecord_ResolvesToEveryRequestRefused()
    {
        var sites = new FakeSiteRepository();
        var permissions = new FakePermissionChecker();
        permissions.Grant(OperatorId, SiteId, Permission.SiteConfigure);
        sites.Seed(new Site(SiteId, "shop_7f3a", ["https://tenant.example"]));
        var signals = new FakeSiteInstallationSignalRepository();
        signals.Seed(SiteId, new SiteInstallationSignals(null, null, "https://www.tenant.example", Now.AddMinutes(-5)));
        var handler = CreateHandler(sites, permissions, signals);

        var result = await handler.HandleAsync(
            new Application.UseCases.GetSiteInstallation.GetSiteInstallation(SiteId, OperatorId), CancellationToken.None);

        Assert.Equal(SiteInstallationState.EveryRequestRefused, result.Value.State);
        Assert.Equal("https://www.tenant.example", result.Value.LastRefusedOrigin);
    }

    /// <summary>A refusal that is *older* than the most recent success is a resolved problem, not a
    /// current one - `SiteInstallationStateResolver`'s own "why a refusal loses to a later success"
    /// reasoning, exercised here through the handler rather than only against the resolver directly.
    /// </summary>
    [Fact]
    public async Task HandleAsync_WhenARefusalPredatesTheMostRecentSighting_ResolvesToSeenAndQuiet()
    {
        var sites = new FakeSiteRepository();
        var permissions = new FakePermissionChecker();
        permissions.Grant(OperatorId, SiteId, Permission.SiteConfigure);
        sites.Seed(new Site(SiteId, "shop_7f3a", ["https://tenant.example"]));
        var signals = new FakeSiteInstallationSignalRepository();
        signals.Seed(
            SiteId,
            new SiteInstallationSignals(
                FirstSeenAt: Now.AddDays(-30),
                LastSeenAt: Now.AddDays(-1),
                LastRefusedOrigin: "https://www.tenant.example",
                LastRefusedOriginAt: Now.AddDays(-10)));
        var handler = CreateHandler(sites, permissions, signals);

        var result = await handler.HandleAsync(
            new Application.UseCases.GetSiteInstallation.GetSiteInstallation(SiteId, OperatorId), CancellationToken.None);

        Assert.Equal(SiteInstallationState.SeenAndQuiet, result.Value.State);
    }

    /// <summary>`23-06`'s own Done-when: "A site never seen whose conversations exist resolves to the
    /// in use, widget unseen state, and the advice for zero loads is not produced for it" - the
    /// channel-only tenant `decisions.md`'s two-facts amendment exists to protect.</summary>
    [Fact]
    public async Task HandleAsync_WhenNeverSeenButAConversationExistsWithinTheWindow_ResolvesToNeverSeenButInUse()
    {
        var sites = new FakeSiteRepository();
        var permissions = new FakePermissionChecker();
        permissions.Grant(OperatorId, SiteId, Permission.SiteConfigure);
        sites.Seed(new Site(SiteId, "shop_7f3a", ["https://tenant.example"]));
        var conversations = new FakeConversationReadStore();
        conversations.Seed(Conversation.Start(new ConversationId(Guid.NewGuid()), SiteId, new VisitorId(Guid.NewGuid()), Now.AddDays(-2)));
        var handler = CreateHandler(sites, permissions, conversations: conversations);

        var result = await handler.HandleAsync(
            new Application.UseCases.GetSiteInstallation.GetSiteInstallation(SiteId, OperatorId), CancellationToken.None);

        Assert.Equal(SiteInstallationState.NeverSeenButInUse, result.Value.State);
        Assert.True(result.Value.UsedRecently);
    }

    /// <summary>The window is configuration (`SiteInstallationOptions.RecentlyThresholdDays`), not a
    /// permanent "ever used" flag - a conversation older than the threshold does not keep this state
    /// alive forever, or a tenant who genuinely stopped using every channel would never be told so.
    /// </summary>
    [Fact]
    public async Task HandleAsync_WhenTheOnlyConversationIsOlderThanTheThreshold_DoesNotResolveToInUse()
    {
        var sites = new FakeSiteRepository();
        var permissions = new FakePermissionChecker();
        permissions.Grant(OperatorId, SiteId, Permission.SiteConfigure);
        sites.Seed(new Site(SiteId, "shop_7f3a", ["https://tenant.example"]));
        var conversations = new FakeConversationReadStore();
        conversations.Seed(Conversation.Start(new ConversationId(Guid.NewGuid()), SiteId, new VisitorId(Guid.NewGuid()), Now.AddDays(-30)));
        var handler = CreateHandler(sites, permissions, conversations: conversations, recentlyThresholdDays: 7);

        var result = await handler.HandleAsync(
            new Application.UseCases.GetSiteInstallation.GetSiteInstallation(SiteId, OperatorId), CancellationToken.None);

        Assert.Equal(SiteInstallationState.NotSeenYet, result.Value.State);
        Assert.False(result.Value.UsedRecently);
    }
}
