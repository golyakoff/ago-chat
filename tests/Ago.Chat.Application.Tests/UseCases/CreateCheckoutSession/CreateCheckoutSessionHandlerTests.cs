using Ago.Chat.Application.Abstractions;
using Ago.Chat.Application.Tests.Fakes;
using Ago.Chat.Application.UseCases.CreateCheckoutSession;
using Ago.Chat.Domain;

namespace Ago.Chat.Application.Tests.UseCases.CreateCheckoutSession;

public class CreateCheckoutSessionHandlerTests
{
    private static readonly SiteId SiteId = new(Guid.NewGuid());
    private static readonly OperatorId OperatorId = new(Guid.NewGuid());
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    private sealed record Fixture(
        CreateCheckoutSessionHandler Handler, FakeBillingSubscriptionRepository Subscriptions, FakeYooKassaPaymentsClient YooKassa,
        FakePriceCatalogRepository Prices);

    // `25-43`: the two seat-pricing keys are seeded straight into the fake catalog, the same "publish
    // once, straight from the aggregate" shortcut FakePriceCatalogRepository.SeedVersion's own remarks
    // describe - a null base/extra price (the "no price configured" branch) is exercised by its own
    // dedicated test below, which builds the handler with an empty, unseeded fake instead.
    private static Fixture CreateFixture(
        bool grantPermission = true, decimal baseSeatPriceRub = 500m, decimal pricePerExtraSeatRub = 50m)
    {
        var sites = new FakeSiteRepository();
        sites.Seed(new Site(SiteId, "shop_7f3a", []));
        var permissions = new FakePermissionChecker();
        if (grantPermission)
        {
            permissions.Grant(OperatorId, SiteId, Permission.SiteConfigure);
        }

        var subscriptions = new FakeBillingSubscriptionRepository();
        var yooKassa = new FakeYooKassaPaymentsClient();
        var prices = new FakePriceCatalogRepository();
        prices.SeedVersion(SubscriptionTierBands.BaseSeatPriceKey, baseSeatPriceRub, Now);
        prices.SeedVersion(SubscriptionTierBands.ExtraSeatPriceKey, pricePerExtraSeatRub, Now);
        var billingOptions = new BillingOptions { CheckoutReturnUrl = "https://console.example/billing/return" };

        var handler = new CreateCheckoutSessionHandler(
            sites, permissions, subscriptions, yooKassa, prices, billingOptions, new FakeIdGenerator(), new FakeClock(Now));

        return new Fixture(handler, subscriptions, yooKassa, prices);
    }

    [Fact]
    public async Task HandleAsync_WhenPermittedAndSeatsAreValid_ReturnsTheConfirmationUrl()
    {
        var fixture = CreateFixture();

        var result = await fixture.Handler.HandleAsync(
            new Application.UseCases.CreateCheckoutSession.CreateCheckoutSession(OperatorId, SiteId, 5), CancellationToken.None);

        Assert.True(result.IsSuccess, result.IsFailure ? result.Error!.Value.Message : null);
        Assert.Equal("https://yookassa.example/confirm", result.Value.ConfirmationUrl);
    }

    // `25-29`: `ago-business` decision `0012`'s banded formula, not the flat `seats × rate` this test
    // used to prove - 5 seats is `SubscriptionTierBands.BaseSeats` (3) plus 2 extra seats, so the
    // charge is `baseSeatPriceRub + 2 × pricePerExtraSeatRub`, never `5 × one rate`.
    [Fact]
    public async Task HandleAsync_ComputesAmount_AsTheBaseSeatPricePlusExtraSeatsPastTheBase()
    {
        var fixture = CreateFixture(baseSeatPriceRub: 500m, pricePerExtraSeatRub: 50m);

        await fixture.Handler.HandleAsync(
            new Application.UseCases.CreateCheckoutSession.CreateCheckoutSession(OperatorId, SiteId, 5), CancellationToken.None);

        Assert.Equal(600m, fixture.YooKassa.LastRequest!.AmountRub);
    }

    // `25-29`: `ago-business` decision `0012`'s own real numbers, proven end to end through this
    // handler rather than only against `SubscriptionTierBands.ComputeSeatPriceRub` directly - the
    // fails-before proof this item's own report cites.
    [Theory]
    [InlineData(2, 490)]
    [InlineData(3, 490)]
    [InlineData(4, 690)]
    [InlineData(5, 890)]
    public async Task HandleAsync_ComputesAmount_MatchingAgoBusinessDecision0012_ForEveryPurchasableSeatCount(
        int requestedSeats, decimal expectedAmountRub)
    {
        var fixture = CreateFixture(baseSeatPriceRub: 490m, pricePerExtraSeatRub: 200m);

        await fixture.Handler.HandleAsync(
            new Application.UseCases.CreateCheckoutSession.CreateCheckoutSession(OperatorId, SiteId, requestedSeats), CancellationToken.None);

        Assert.Equal(expectedAmountRub, fixture.YooKassa.LastRequest!.AmountRub);
    }

    [Fact]
    public async Task HandleAsync_WhenSuccessful_RecordsAPendingSubscriptionWithTheResolvedTier()
    {
        var fixture = CreateFixture();

        await fixture.Handler.HandleAsync(
            new Application.UseCases.CreateCheckoutSession.CreateCheckoutSession(OperatorId, SiteId, 5), CancellationToken.None);

        var saved = Assert.Single(fixture.Subscriptions.Saved);
        Assert.Equal(SiteId, saved.SiteId);
        Assert.Equal("pmt_fake", saved.YooKassaPaymentId);
        Assert.Equal(5, saved.RequestedSeats);
        Assert.Equal(SubscriptionTierBands.Starter, saved.Tier);
        Assert.Equal(BillingSubscriptionStatus.Pending, saved.Status);
    }

    [Fact]
    public async Task HandleAsync_WhenTheOperatorLacksSiteConfigure_ReturnsForbidden_AndCallsNeitherYooKassaNorSaves()
    {
        var fixture = CreateFixture(grantPermission: false);

        var result = await fixture.Handler.HandleAsync(
            new Application.UseCases.CreateCheckoutSession.CreateCheckoutSession(OperatorId, SiteId, 5), CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Conversation.Forbidden", result.Error!.Value.Code);
        Assert.Null(fixture.YooKassa.LastRequest);
        Assert.Empty(fixture.Subscriptions.Saved);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(6)] // `25-29`: the first seat count past `0012`'s own Business band - 2 (the free
                    // tier's own former ceiling, `13-08`) is now a purchasable count in its own right.
    [InlineData(101)]
    public async Task HandleAsync_WhenSeatsAreOutsideTheBandTable_ReturnsInvalidSeatCount_AndNeverCallsYooKassa(int requestedSeats)
    {
        var fixture = CreateFixture();

        var result = await fixture.Handler.HandleAsync(
            new Application.UseCases.CreateCheckoutSession.CreateCheckoutSession(OperatorId, SiteId, requestedSeats), CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Billing.InvalidSeatCount", result.Error!.Value.Code);
        Assert.Null(fixture.YooKassa.LastRequest);
    }

    // `25-43`'s own Done-when, proven here at the first real charge site: "a key with no published
    // version refuses any charge attempt cleanly and namedly ... never a crash, never a zero-amount
    // charge." An unseeded FakePriceCatalogRepository is the honest fixture for "built, not yet for
    // sale" - the ordinary state this item's own second decision names, not a fault this test needs
    // to force through some other mechanism.
    [Theory]
    [InlineData(true, false)] // only the extra-seat key is missing
    [InlineData(false, true)] // only the base-seat key is missing
    [InlineData(false, false)] // neither key has ever been published
    public async Task HandleAsync_WhenEitherSeatPricingKeyHasNoPublishedVersion_ReturnsPriceNotConfigured_AndNeverCallsYooKassaOrSaves(
        bool seedBase, bool seedExtra)
    {
        var sites = new FakeSiteRepository();
        sites.Seed(new Site(SiteId, "shop_7f3a", []));
        var permissions = new FakePermissionChecker();
        permissions.Grant(OperatorId, SiteId, Permission.SiteConfigure);
        var subscriptions = new FakeBillingSubscriptionRepository();
        var yooKassa = new FakeYooKassaPaymentsClient();
        var prices = new FakePriceCatalogRepository();
        if (seedBase)
        {
            prices.SeedVersion(SubscriptionTierBands.BaseSeatPriceKey, 500m, Now);
        }

        if (seedExtra)
        {
            prices.SeedVersion(SubscriptionTierBands.ExtraSeatPriceKey, 50m, Now);
        }

        var handler = new CreateCheckoutSessionHandler(
            sites, permissions, subscriptions, yooKassa, prices, new BillingOptions { CheckoutReturnUrl = "https://console.example/billing/return" },
            new FakeIdGenerator(), new FakeClock(Now));

        var result = await handler.HandleAsync(
            new Application.UseCases.CreateCheckoutSession.CreateCheckoutSession(OperatorId, SiteId, 5), CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Billing.PriceNotConfigured", result.Error!.Value.Code);
        // Never a crash (a Result, not a thrown exception) and never a zero-amount charge - YooKassa
        // is never even called, let alone charged Rub 0, and no subscription row is saved as if a
        // charge had actually gone through.
        Assert.Null(yooKassa.LastRequest);
        Assert.Empty(subscriptions.Saved);
    }

    [Fact]
    public async Task HandleAsync_WhenYooKassaRefuses_ReturnsPaymentProviderRefused_AndSavesNoSubscription()
    {
        var fixture = CreateFixture();
        fixture.YooKassa.Result = new CreatePaymentResult.Refused("insufficient funds");

        var result = await fixture.Handler.HandleAsync(
            new Application.UseCases.CreateCheckoutSession.CreateCheckoutSession(OperatorId, SiteId, 5), CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Billing.PaymentProviderRefused", result.Error!.Value.Code);
        Assert.Empty(fixture.Subscriptions.Saved);
    }

    [Fact]
    public async Task HandleAsync_WhenSiteDoesNotExist_ReturnsSiteNotFound()
    {
        // A site the caller is permitted on but that ISiteRepository has no row for - the permission
        // check alone cannot distinguish "does not exist" from "not yours", so this seeds the grant
        // against a SiteId no Site was ever seeded for, isolating the SiteNotFound branch from the
        // Forbidden one the test right above already covers.
        var missingSiteId = new SiteId(Guid.NewGuid());
        var extraPermissions = new FakePermissionChecker();
        extraPermissions.Grant(OperatorId, missingSiteId, Permission.SiteConfigure);
        var missingSitePrices = new FakePriceCatalogRepository();
        missingSitePrices.SeedVersion(SubscriptionTierBands.BaseSeatPriceKey, 500m, Now);
        missingSitePrices.SeedVersion(SubscriptionTierBands.ExtraSeatPriceKey, 50m, Now);
        var handler = new CreateCheckoutSessionHandler(
            new FakeSiteRepository(), extraPermissions, new FakeBillingSubscriptionRepository(), new FakeYooKassaPaymentsClient(),
            missingSitePrices, new BillingOptions { CheckoutReturnUrl = "https://console.example/billing/return" },
            new FakeIdGenerator(), new FakeClock(Now));

        var result = await handler.HandleAsync(
            new Application.UseCases.CreateCheckoutSession.CreateCheckoutSession(OperatorId, missingSiteId, 5), CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Site.NotFound", result.Error!.Value.Code);
    }
}
