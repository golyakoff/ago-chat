using Ago.Chat.Application.Tests.Fakes;
using Ago.Chat.Application.UseCases.SetDownloadOverageBillingModeAsOwner;
using Ago.Chat.Domain;

namespace Ago.Chat.Application.Tests.UseCases.SetDownloadOverageBillingModeAsOwner;

/// <summary>`25-84`: the platform owner's own per-tenant toggle - `docs/backlog/25-84-*.md`'s own
/// Done-when ("the platform owner can set the auto-bill/manual toggle per tenant"), and its own Out of
/// scope (no tenant ever reaches this, which the route's `RequirePlatformOwner` policy enforces and
/// `OwnerDownloadOverageBillingModeEndpointTests` proves over real HTTP).</summary>
public class SetDownloadOverageBillingModeAsOwnerHandlerTests
{
    private static readonly SiteId SiteId = new(Guid.NewGuid());
    private static readonly DateTimeOffset Now = new(2026, 9, 14, 12, 0, 0, TimeSpan.Zero);

    private static (SetDownloadOverageBillingModeAsOwnerHandler Handler, FakeSiteRepository Sites, Site Site) CreateFixture()
    {
        var site = new Site(SiteId, "pk-" + SiteId.Value, allowedOrigins: []);
        var sites = new FakeSiteRepository();
        sites.Seed(site);
        return (new SetDownloadOverageBillingModeAsOwnerHandler(sites, new FakeClock(Now)), sites, site);
    }

    /// <summary>Every site starts on <see cref="DownloadOverageBillingMode.Manual"/> - the behaviour
    /// `25-83` already shipped. See that enum's own remarks for why the backlog's "recommended default"
    /// is a recommendation to the owner rather than a migration that starts charging everybody.</summary>
    [Fact]
    public void ANewSite_StartsOnManual_SoNothingIsEverChargedWithoutAnOwnerActing()
    {
        var (_, _, site) = CreateFixture();

        Assert.Equal(DownloadOverageBillingMode.Manual, site.DownloadOverageBillingMode);
        Assert.Null(site.DownloadOverageBillingModeChangedBy);
    }

    [Fact]
    public async Task HandleAsync_SetsTheMode_AndRecordsWhoChangedItAndWhy()
    {
        var (handler, _, site) = CreateFixture();

        var result = await handler.HandleAsync(
            new global::Ago.Chat.Application.UseCases.SetDownloadOverageBillingModeAsOwner.SetDownloadOverageBillingModeAsOwner(
                SiteId, DownloadOverageBillingMode.AutoBill, "owner-sub", "  agreed on the onboarding call  "),
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(DownloadOverageBillingMode.AutoBill, site.DownloadOverageBillingMode);
        Assert.Equal("owner-sub", site.DownloadOverageBillingModeChangedBy);
        Assert.Equal("agreed on the onboarding call", site.DownloadOverageBillingModeReason);
        Assert.Equal(Now, site.DownloadOverageBillingModeChangedAt);
    }

    /// <summary>Both directions, not just the one that starts charging - moving a tenant back onto
    /// manual can block them, which is equally worth a stated reason.</summary>
    [Fact]
    public async Task HandleAsync_MovesBackToManual_RecordingTheReasonForThatToo()
    {
        var (handler, _, site) = CreateFixture();
        site.SetDownloadOverageBillingMode(DownloadOverageBillingMode.AutoBill, "owner-sub", "first decision", Now);

        var result = await handler.HandleAsync(
            new global::Ago.Chat.Application.UseCases.SetDownloadOverageBillingModeAsOwner.SetDownloadOverageBillingModeAsOwner(
                SiteId, DownloadOverageBillingMode.Manual, "other-owner", "tenant asked to be asked first"),
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(DownloadOverageBillingMode.Manual, site.DownloadOverageBillingMode);
        Assert.Equal("tenant asked to be asked first", site.DownloadOverageBillingModeReason);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task HandleAsync_WithoutAReason_IsRefused_AndChangesNothing(string reason)
    {
        var (handler, _, site) = CreateFixture();

        var result = await handler.HandleAsync(
            new global::Ago.Chat.Application.UseCases.SetDownloadOverageBillingModeAsOwner.SetDownloadOverageBillingModeAsOwner(
                SiteId, DownloadOverageBillingMode.AutoBill, "owner-sub", reason),
            CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Site.DownloadOverageBillingModeReasonRequired", result.Error!.Value.Code);
        Assert.Equal(DownloadOverageBillingMode.Manual, site.DownloadOverageBillingMode);
    }

    [Fact]
    public async Task HandleAsync_WithAnOverLongReason_IsRefused_AndChangesNothing()
    {
        var (handler, _, site) = CreateFixture();

        var result = await handler.HandleAsync(
            new global::Ago.Chat.Application.UseCases.SetDownloadOverageBillingModeAsOwner.SetDownloadOverageBillingModeAsOwner(
                SiteId, DownloadOverageBillingMode.AutoBill, "owner-sub",
                new string('x', SetDownloadOverageBillingModeAsOwnerHandler.MaxReasonLength + 1)),
            CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Site.DownloadOverageBillingModeReasonRequired", result.Error!.Value.Code);
        Assert.Equal(DownloadOverageBillingMode.Manual, site.DownloadOverageBillingMode);
    }

    [Fact]
    public async Task HandleAsync_ForASiteThatDoesNotExist_IsRefused()
    {
        var (handler, _, _) = CreateFixture();

        var result = await handler.HandleAsync(
            new global::Ago.Chat.Application.UseCases.SetDownloadOverageBillingModeAsOwner.SetDownloadOverageBillingModeAsOwner(
                new SiteId(Guid.NewGuid()), DownloadOverageBillingMode.AutoBill, "owner-sub", "why"),
            CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Site.NotFound", result.Error!.Value.Code);
    }
}
