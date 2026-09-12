using Ago.Chat.Application.Tests.Fakes;
using Ago.Chat.Application.UseCases.GetBillingStatus;
using Ago.Chat.Domain;

namespace Ago.Chat.Application.Tests.UseCases.GetBillingStatus;

/// <summary>`13-04`: the console billing screen's own bootstrap read, proven at the handler level -
/// permission gate, site-not-found, no-subscription-yet, and the honest `Pending` shape a caller
/// returning from ЮKassa's hosted checkout polls against (this file's own last two tests).</summary>
public class GetBillingStatusHandlerTests
{
    private static readonly SiteId SiteId = new(Guid.NewGuid());
    private static readonly OperatorId RequestedBy = new(Guid.NewGuid());
    private static readonly DateTimeOffset Now = new(2026, 8, 29, 12, 0, 0, TimeSpan.Zero);

    private sealed record Fixture(
        GetBillingStatusHandler Handler, FakeBillingSubscriptionRepository Subscriptions, FakeOperatorRepository Operators,
        FakeOperatorRoleRepository OperatorRoles, FakePriceCatalogRepository Prices);

    // `25-23`: both seat-pricing keys are seeded by default - the handler throws otherwise (the
    // identical "unreachable on a deployment whose migration seed ran" judgement GetPricingForOwnerHandler
    // already makes, restated here so every existing test in this file - none of which is about pricing -
    // does not have to seed it by hand.
    private static Fixture CreateFixture(string tier = "free", int seatLimit = 1, bool grantPermission = true)
    {
        var sites = new FakeSiteRepository();
        sites.Seed(new Site(SiteId, $"site_{SiteId.Value:N}", [], tier: tier, seatLimit: seatLimit));

        var operators = new FakeOperatorRepository();
        var subscriptions = new FakeBillingSubscriptionRepository();
        var operatorRoles = new FakeOperatorRoleRepository();

        var prices = new FakePriceCatalogRepository();
        prices.SeedVersion(SubscriptionTierBands.BaseSeatPriceKey, 490m, Now);
        prices.SeedVersion(SubscriptionTierBands.ExtraSeatPriceKey, 200m, Now);

        var permissions = new FakePermissionChecker();
        if (grantPermission)
        {
            permissions.Grant(RequestedBy, SiteId, Permission.SiteConfigure);
        }

        var handler = new GetBillingStatusHandler(sites, operators, subscriptions, operatorRoles, prices, permissions);
        return new Fixture(handler, subscriptions, operators, operatorRoles, prices);
    }

    [Fact]
    public async Task HandleAsync_WhenCallerLacksSiteConfigure_ReturnsForbidden_AndLeaksNoBillingData()
    {
        var fixture = CreateFixture(tier: SubscriptionTierBands.Starter, seatLimit: 5, grantPermission: false);

        var result = await fixture.Handler.HandleAsync(new Application.UseCases.GetBillingStatus.GetBillingStatus(RequestedBy, SiteId), CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Conversation.Forbidden", result.Error!.Value.Code);
    }

    [Fact]
    public async Task HandleAsync_WhenSiteHasNeverCheckedOut_ReturnsFreeTierWithNoLatestSubscription()
    {
        var fixture = CreateFixture(tier: "free", seatLimit: 1);
        fixture.Operators.Seed(new Operator(new OperatorId(Guid.NewGuid()), SiteId, OperatorStatus.Offline, capacity: 5));

        var result = await fixture.Handler.HandleAsync(new Application.UseCases.GetBillingStatus.GetBillingStatus(RequestedBy, SiteId), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal("free", result.Value.Tier);
        Assert.Equal(1, result.Value.SeatLimit);
        Assert.Equal(1, result.Value.SeatsUsed);
        Assert.Null(result.Value.LatestSubscription);
        // `25-23`: the free tier's own name, its Administrator ceiling, and a never-purchased extra
        // Administrator count - the ordinary state for a site that has never checked out at all.
        Assert.Equal("Solo", result.Value.TierDisplayName);
        Assert.Equal(SubscriptionTierBands.FreeAdminsIncluded, result.Value.AdminLimit);
        Assert.Equal(0, result.Value.AdminsUsed);
        Assert.Equal(0, result.Value.ExtraAdministratorsPurchased);
        Assert.Null(result.Value.AdminExtraPriceRub);
        Assert.Equal(SubscriptionTierBands.MinSeats, result.Value.SeatPricing.MinSeats);
        Assert.Equal(SubscriptionTierBands.MaxSeats, result.Value.SeatPricing.MaxSeats);
        Assert.Equal(SubscriptionTierBands.BaseSeats, result.Value.SeatPricing.BaseSeats);
        Assert.Equal(SubscriptionTierBands.FreeSeatsIncluded, result.Value.SeatPricing.FreeSeatsIncluded);
        Assert.Equal(490m, result.Value.SeatPricing.BaseSeatPriceRub);
        Assert.Equal(200m, result.Value.SeatPricing.PricePerExtraSeatRub);
        Assert.Equal(BillingSubscription.PeriodLength.TotalDays, result.Value.SeatPricing.BillingPeriodDays);
    }

    // `25-23`: the business name `ago-business 0012` actually uses for its one paid tier - the mapping
    // decision this item's own Scope asks to be made and stated (server-side, see BillingStatusDto's
    // own remarks on TierDisplayName).
    [Fact]
    public async Task HandleAsync_WhenTierIsPaid_MapsTierDisplayNameToBusiness_NotTheRawEnumValue()
    {
        var fixture = CreateFixture(tier: SubscriptionTierBands.Starter, seatLimit: 5);

        var result = await fixture.Handler.HandleAsync(new Application.UseCases.GetBillingStatus.GetBillingStatus(RequestedBy, SiteId), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(SubscriptionTierBands.Starter, result.Value.Tier);
        Assert.Equal("Business", result.Value.TierDisplayName);
        Assert.Equal(SubscriptionTierBands.BusinessAdminsIncluded, result.Value.AdminLimit);
    }

    // `25-23`: AdminsUsed counts only non-removed Administrator role holders - a removed one, and an
    // Operator who is not an Administrator at all, must not inflate this count.
    [Fact]
    public async Task HandleAsync_CountsOnlyNonRemovedAdministrators_SeparatelyFromOperatorSeats()
    {
        var fixture = CreateFixture(tier: SubscriptionTierBands.Starter, seatLimit: 5);
        var admin = new OperatorId(Guid.NewGuid());
        var plainOperator = new OperatorId(Guid.NewGuid());
        fixture.OperatorRoles.Seed(admin, "Admin");
        fixture.OperatorRoles.Seed(plainOperator, "Operator");

        var result = await fixture.Handler.HandleAsync(new Application.UseCases.GetBillingStatus.GetBillingStatus(RequestedBy, SiteId), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(1, result.Value.AdminsUsed);
    }

    // `25-23`: the free-vs-paid-beyond-it split's own real number, read straight off the base
    // subscription's own ExtraAdministratorsPurchased field - `25-41`'s fact, not re-derived here.
    [Fact]
    public async Task HandleAsync_ReportsExtraAdministratorsPurchased_FromTheBaseSubscriptionsOwnField()
    {
        var fixture = CreateFixture(tier: SubscriptionTierBands.Starter, seatLimit: 5);
        var subscription = BillingSubscription.Create(
            new BillingSubscriptionId(Guid.NewGuid()), SiteId, "yk_payment_1", requestedSeats: 5, tier: SubscriptionTierBands.Starter,
            baseSeatPriceVersion: 1, extraSeatPriceVersion: 1, createdAt: Now);
        subscription.MarkSucceeded("card_abc", Now);
        subscription.ApplyAdministratorPurchase(2, adminExtraPriceVersion: 1);
        fixture.Subscriptions.Seed(subscription);
        fixture.Prices.SeedVersion(SubscriptionTierBands.AdminExtraPriceKey, 500m, Now);

        var result = await fixture.Handler.HandleAsync(new Application.UseCases.GetBillingStatus.GetBillingStatus(RequestedBy, SiteId), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(2, result.Value.ExtraAdministratorsPurchased);
        Assert.Equal(500m, result.Value.AdminExtraPriceRub);
    }

    // `25-23`/`25-43`: the identical "unreachable on a deployment whose migration seed ran" loud
    // failure GetPricingForOwnerHandler already throws for these same two keys - proven here rather
    // than assumed to carry over silently.
    [Fact]
    public async Task HandleAsync_WhenSeatPricingHasNoPublishedVersion_ThrowsRatherThanFabricatingAPrice()
    {
        var sites = new FakeSiteRepository();
        sites.Seed(new Site(SiteId, $"site_{SiteId.Value:N}", [], tier: "free", seatLimit: 1));
        var permissions = new FakePermissionChecker();
        permissions.Grant(RequestedBy, SiteId, Permission.SiteConfigure);
        var handler = new GetBillingStatusHandler(
            sites, new FakeOperatorRepository(), new FakeBillingSubscriptionRepository(),
            new FakeOperatorRoleRepository(), new FakePriceCatalogRepository(), permissions);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => handler.HandleAsync(new Application.UseCases.GetBillingStatus.GetBillingStatus(RequestedBy, SiteId), CancellationToken.None));
    }

    [Fact]
    public async Task HandleAsync_WhenLatestSubscriptionIsPending_ReportsPendingStatus_NotSucceeded()
    {
        var fixture = CreateFixture(tier: "free", seatLimit: 1);
        var subscription = BillingSubscription.Create(
            new BillingSubscriptionId(Guid.NewGuid()), SiteId, "yk_payment_1", requestedSeats: 5, tier: SubscriptionTierBands.Starter, baseSeatPriceVersion: 1, extraSeatPriceVersion: 1, createdAt: Now);
        fixture.Subscriptions.Seed(subscription);

        var result = await fixture.Handler.HandleAsync(new Application.UseCases.GetBillingStatus.GetBillingStatus(RequestedBy, SiteId), CancellationToken.None);

        Assert.True(result.IsSuccess);
        // The site's own Tier/SeatLimit are untouched - only a verified webhook moves them, never the
        // checkout-session call. A caller must not read this as "confirmed" just because a row exists.
        Assert.Equal("free", result.Value.Tier);
        Assert.Equal(1, result.Value.SeatLimit);
        Assert.NotNull(result.Value.LatestSubscription);
        Assert.Equal("Pending", result.Value.LatestSubscription!.Status);
        Assert.Equal(5, result.Value.LatestSubscription.RequestedSeats);
    }

    [Fact]
    public async Task HandleAsync_WhenLatestSubscriptionHasSucceeded_ReportsSucceededStatus_AndSiteEntitlements()
    {
        var fixture = CreateFixture(tier: SubscriptionTierBands.Starter, seatLimit: 5);
        var subscription = BillingSubscription.Create(
            new BillingSubscriptionId(Guid.NewGuid()), SiteId, "yk_payment_1", requestedSeats: 5, tier: SubscriptionTierBands.Starter, baseSeatPriceVersion: 1, extraSeatPriceVersion: 1, createdAt: Now);
        subscription.MarkSucceeded("card_abc", Now);
        fixture.Subscriptions.Seed(subscription);

        var result = await fixture.Handler.HandleAsync(new Application.UseCases.GetBillingStatus.GetBillingStatus(RequestedBy, SiteId), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(SubscriptionTierBands.Starter, result.Value.Tier);
        Assert.Equal(5, result.Value.SeatLimit);
        Assert.Equal("Succeeded", result.Value.LatestSubscription!.Status);
        Assert.False(result.Value.LatestSubscription.CancelRequested);
        Assert.Null(result.Value.LatestSubscription.PendingSeatCount);
    }

    [Fact]
    public async Task HandleAsync_WhenMultipleSubscriptionsExist_ReturnsOnlyTheMostRecentlyCreatedOne()
    {
        var fixture = CreateFixture(tier: "free", seatLimit: 1);
        var older = BillingSubscription.Create(
            new BillingSubscriptionId(Guid.NewGuid()), SiteId, "yk_payment_old", requestedSeats: 3, tier: SubscriptionTierBands.Starter, baseSeatPriceVersion: 1, extraSeatPriceVersion: 1, createdAt: Now - TimeSpan.FromDays(60));
        older.MarkFailed();
        var newer = BillingSubscription.Create(
            new BillingSubscriptionId(Guid.NewGuid()), SiteId, "yk_payment_new", requestedSeats: 5, tier: SubscriptionTierBands.Starter, baseSeatPriceVersion: 1, extraSeatPriceVersion: 1, createdAt: Now);
        fixture.Subscriptions.Seed(older);
        fixture.Subscriptions.Seed(newer);

        var result = await fixture.Handler.HandleAsync(new Application.UseCases.GetBillingStatus.GetBillingStatus(RequestedBy, SiteId), CancellationToken.None);

        Assert.Equal(newer.Id.Value, result.Value.LatestSubscription!.SubscriptionId);
        Assert.Equal("Pending", result.Value.LatestSubscription.Status);
    }

    /// <summary>`23-86`/`adr/0159`: the trap this item's own brief names by name - "a tenant who just
    /// bought a channel sees the channel where their tier belongs". A purchased option is newer than
    /// the base and lives in the identical table; this handler must still report the base's own tier
    /// and seats, and must not surface the option row as `LatestSubscription` in its place.</summary>
    [Fact]
    public async Task HandleAsync_WhenAnOptionWasPurchasedAfterTheBase_StillReportsTheBaseSubscription_NotTheOption()
    {
        var fixture = CreateFixture(tier: SubscriptionTierBands.Starter, seatLimit: 5);
        var baseSubscription = BillingSubscription.Create(
            new BillingSubscriptionId(Guid.NewGuid()), SiteId, "yk_payment_base", requestedSeats: 5, tier: SubscriptionTierBands.Starter,
            baseSeatPriceVersion: 1, extraSeatPriceVersion: 1, createdAt: Now - TimeSpan.FromDays(10));
        baseSubscription.MarkSucceeded("card_abc", Now - TimeSpan.FromDays(10));
        fixture.Subscriptions.Seed(baseSubscription);

        // Bought after the base, so it is the newest row for this site - GetLatestForSiteAsync would
        // hand this row back; GetBaseForSiteAsync must not.
        var option = BillingSubscription.CreateOption(
            new BillingSubscriptionId(Guid.NewGuid()), SiteId, "yk_payment_option", new BillingOptionKey("channel-telegram"), Now);
        option.MarkSucceeded("card_abc", Now, alignedPeriodEnd: baseSubscription.CurrentPeriodEnd);
        fixture.Subscriptions.Seed(option);

        var result = await fixture.Handler.HandleAsync(new Application.UseCases.GetBillingStatus.GetBillingStatus(RequestedBy, SiteId), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(SubscriptionTierBands.Starter, result.Value.Tier);
        Assert.Equal(5, result.Value.SeatLimit);
        Assert.Equal(baseSubscription.Id.Value, result.Value.LatestSubscription!.SubscriptionId);
        Assert.Equal(5, result.Value.LatestSubscription.RequestedSeats);
    }
}
