using Ago.Chat.Application.Abstractions;
using Ago.Chat.Application.Tests.Fakes;
using Ago.Chat.Application.UseCases.ChangeSubscriptionSeats;
using Ago.Chat.Application.UseCases.CreateCheckoutSession;
using Ago.Chat.Domain;

namespace Ago.Chat.Application.Tests.UseCases.ChangeSubscriptionSeats;

public class ChangeSubscriptionSeatsHandlerTests
{
    private static readonly SiteId SiteId = new(Guid.NewGuid());
    private static readonly OperatorId OperatorId = new(Guid.NewGuid());
    private static readonly DateTimeOffset Now = new(2026, 8, 29, 12, 0, 0, TimeSpan.Zero);

    private sealed record Fixture(
        Application.UseCases.ChangeSubscriptionSeats.ChangeSubscriptionSeatsHandler Handler,
        FakeBillingSubscriptionRepository Subscriptions,
        FakeYooKassaPaymentsClient YooKassa,
        FakeSeatChangeApplier Applier,
        FakePriceCatalogRepository Prices);

    // `25-43`: both keys published once, at Now - the same value old and new, since these tests exist
    // to prove the proration formula itself, not a price change mid-flow (that is
    // ProcessSubscriptionRenewalHandlerTests'/PriceCatalogRepositoryTests' own scope).
    private static Fixture CreateFixture()
    {
        var subscriptions = new FakeBillingSubscriptionRepository();
        var permissions = new FakePermissionChecker();
        permissions.Grant(OperatorId, SiteId, Permission.SiteConfigure);
        var yooKassa = new FakeYooKassaPaymentsClient();
        var applier = new FakeSeatChangeApplier();
        var prices = new FakePriceCatalogRepository();
        prices.SeedVersion(SubscriptionTierBands.BaseSeatPriceKey, 500m, Now);
        prices.SeedVersion(SubscriptionTierBands.ExtraSeatPriceKey, 100m, Now);

        var handler = new Application.UseCases.ChangeSubscriptionSeats.ChangeSubscriptionSeatsHandler(
            subscriptions, permissions, yooKassa, prices, applier,
            new FakeIdGenerator(), new FakeClock(Now));

        return new Fixture(handler, subscriptions, yooKassa, applier, prices);
    }

    private static async Task<BillingSubscription> SeedSucceededAsync(
        Fixture fixture, BillingSubscriptionId id, int seats, string tier, DateTimeOffset? succeededAt = null)
    {
        var when = succeededAt ?? Now - BillingSubscription.PeriodLength;
        // `25-43`: the subscription's own stored price version is whatever the catalog's current
        // answer is at seed time - ApplyUpgradeAsync's own "oldPrice reads the subscription's stored
        // version, never the catalog's current one" guard means this must be a real, resolvable
        // sequence, not a placeholder int.
        var baseVersion = await fixture.Prices.FindCurrentAsync(SubscriptionTierBands.BaseSeatPriceKey, CancellationToken.None);
        var extraVersion = await fixture.Prices.FindCurrentAsync(SubscriptionTierBands.ExtraSeatPriceKey, CancellationToken.None);
        var subscription = BillingSubscription.Create(id, SiteId, "pmt_123", seats, tier, baseVersion!.Sequence, extraVersion!.Sequence, when);
        subscription.MarkSucceeded("card_abc", when);
        fixture.Subscriptions.Seed(subscription);
        return subscription;
    }

    [Fact]
    public async Task HandleAsync_WhenSeatCountIsUnchanged_ReturnsError()
    {
        var fixture = CreateFixture();
        var id = new BillingSubscriptionId(Guid.NewGuid());
        await SeedSucceededAsync(fixture, id, seats: 5, tier: SubscriptionTierBands.Starter);

        var result = await fixture.Handler.HandleAsync(
            new Application.UseCases.ChangeSubscriptionSeats.ChangeSubscriptionSeats(OperatorId, SiteId, id, 5), CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Billing.SeatCountUnchanged", result.Error!.Value.Code);
    }

    // `25-29`: upgrading 3 -> 5 seats (both within `ago-business` decision `0012`'s own Business band)
    // rather than the old 5 -> 15 this test used against `0008`'s superseded, uncapped grid - 15 seats
    // is no longer a purchasable count at all (`SubscriptionTierBands.MaxSeats` is 5).
    [Fact]
    public async Task HandleAsync_WhenAnUpgrade_ChargesTheProratedDifference_AndAppliesImmediately()
    {
        var fixture = CreateFixture();
        var id = new BillingSubscriptionId(Guid.NewGuid());
        // Succeeded half a period ago, so CurrentPeriodEnd sits exactly half a period into the future
        // from Now - a clean, checkable remaining-days fraction (0.5) computed honestly through
        // MarkSucceeded's own "CurrentPeriodEnd = succeededAt + PeriodLength" rule, not forced.
        var halfPeriodAgo = Now - TimeSpan.FromDays(BillingSubscription.PeriodLength.TotalDays / 2);
        await SeedSucceededAsync(fixture, id, seats: 3, tier: SubscriptionTierBands.Starter, succeededAt: halfPeriodAgo);

        var result = await fixture.Handler.HandleAsync(
            new Application.UseCases.ChangeSubscriptionSeats.ChangeSubscriptionSeats(OperatorId, SiteId, id, 5), CancellationToken.None);

        Assert.True(result.IsSuccess);
        var upgraded = Assert.IsType<ChangeSubscriptionSeatsResult.Upgraded>(result.Value);
        // old (3 seats, at the base) = 500 + 0×100 = 500; new (5 seats, 2 past the base) =
        // 500 + 2×100 = 700; (700 - 500) × 0.5 = 100.00
        Assert.Equal(100.00m, upgraded.ProratedAmountRub);
        Assert.NotNull(fixture.YooKassa.LastChargeRequest);
        Assert.Equal(100.00m, fixture.YooKassa.LastChargeRequest!.AmountRub);
        Assert.Single(fixture.Applier.Applied);
        Assert.Equal(5, fixture.Applier.Applied[0].NewSeatCount);
    }

    // `25-43`'s own Done-when, proven end to end at a real charge site: "every charge site reads the
    // currently-effective version, by key, at the moment it charges ... a test that changes the
    // effective price mid-flow and shows the charge already in progress is unaffected while the next
    // one picks up the new value." The "charge already in progress" here is the subscription's own
    // stored BaseSeatPriceVersion/ExtraSeatPriceVersion - what it was actually last charged under - and
    // this test proves the proration's own "old" side keeps reading that historical fact even after
    // the catalog moves on, while the "new" side picks up the new figure precisely because it reads
    // FindCurrentAsync fresh (ChangeSubscriptionSeatsHandler's own remarks on why oldPrice/newPrice
    // deliberately read two different methods).
    [Fact]
    public async Task HandleAsync_WhenThePriceChangesAfterTheLastChargeButBeforeThisUpgrade_ProratesTheHistoricalOldPriceAgainstTheNewCurrentPrice()
    {
        var fixture = CreateFixture();
        var id = new BillingSubscriptionId(Guid.NewGuid());
        var halfPeriodAgo = Now - TimeSpan.FromDays(BillingSubscription.PeriodLength.TotalDays / 2);
        // Charged at CreateFixture's own 500/100 (v1) half a period ago - this subscription's own
        // stored BaseSeatPriceVersion/ExtraSeatPriceVersion now names that historical fact.
        await SeedSucceededAsync(fixture, id, seats: 3, tier: SubscriptionTierBands.Starter, succeededAt: halfPeriodAgo);

        // The platform owner publishes a new price for both keys before this upgrade ever happens -
        // the catalog's own "currently effective" answer moves to 600/150 (v2), but the subscription's
        // own stored v1 is left exactly as it was.
        fixture.Prices.SeedVersion(SubscriptionTierBands.BaseSeatPriceKey, 600m, Now);
        fixture.Prices.SeedVersion(SubscriptionTierBands.ExtraSeatPriceKey, 150m, Now);

        var result = await fixture.Handler.HandleAsync(
            new Application.UseCases.ChangeSubscriptionSeats.ChangeSubscriptionSeats(OperatorId, SiteId, id, 5), CancellationToken.None);

        Assert.True(result.IsSuccess);
        var upgraded = Assert.IsType<ChangeSubscriptionSeatsResult.Upgraded>(result.Value);
        // old (3 seats, at the base, read from the subscription's own stored v1) = 500 + 0x100 = 500;
        // new (5 seats, 2 past the base, read from the catalog's own current v2) = 600 + 2x150 = 900;
        // (900 - 500) x 0.5 = 200.00 - never (900 - 900) or (500 - 500), which either "always re-price
        // the old side too" or "never notice the new price at all" would silently produce instead.
        Assert.Equal(200.00m, upgraded.ProratedAmountRub);
        Assert.NotNull(fixture.YooKassa.LastChargeRequest);
        Assert.Equal(200.00m, fixture.YooKassa.LastChargeRequest!.AmountRub);

        // The seat change this upgrade actually applies going forward stores the NEW (v2) version
        // numbers - the next renewal, and the next upgrade after this one, both charge at 600/150
        // without needing to re-read anything from this request.
        var applied = Assert.Single(fixture.Applier.Applied);
        var newBaseVersion = await fixture.Prices.FindCurrentAsync(SubscriptionTierBands.BaseSeatPriceKey, CancellationToken.None);
        var newExtraVersion = await fixture.Prices.FindCurrentAsync(SubscriptionTierBands.ExtraSeatPriceKey, CancellationToken.None);
        Assert.Equal(newBaseVersion!.Sequence, applied.BaseSeatPriceVersion);
        Assert.Equal(newExtraVersion!.Sequence, applied.ExtraSeatPriceVersion);
    }

    [Fact]
    public async Task HandleAsync_WhenAnUpgradeIsRefused_ReturnsError_AndAppliesNothing()
    {
        var fixture = CreateFixture();
        var id = new BillingSubscriptionId(Guid.NewGuid());
        await SeedSucceededAsync(fixture, id, seats: 3, tier: SubscriptionTierBands.Starter);
        fixture.YooKassa.ChargeResult = new ChargeStoredPaymentMethodResult.Refused("card declined");

        var result = await fixture.Handler.HandleAsync(
            new Application.UseCases.ChangeSubscriptionSeats.ChangeSubscriptionSeats(OperatorId, SiteId, id, 5), CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Billing.PaymentProviderRefused", result.Error!.Value.Code);
        Assert.Empty(fixture.Applier.Applied);
    }

    [Fact]
    public async Task HandleAsync_WhenADowngrade_SchedulesItWithNoChargeAndNoImmediateApply()
    {
        var fixture = CreateFixture();
        var id = new BillingSubscriptionId(Guid.NewGuid());
        var subscription = await SeedSucceededAsync(fixture, id, seats: 20, tier: SubscriptionTierBands.Growth);

        var result = await fixture.Handler.HandleAsync(
            new Application.UseCases.ChangeSubscriptionSeats.ChangeSubscriptionSeats(OperatorId, SiteId, id, 5), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.IsType<ChangeSubscriptionSeatsResult.DowngradeScheduled>(result.Value);
        Assert.Null(fixture.YooKassa.LastChargeRequest);
        Assert.Empty(fixture.Applier.Applied);
        Assert.Equal(5, subscription.PendingSeatCount);
        Assert.Equal(20, subscription.RequestedSeats);
        Assert.Single(fixture.Subscriptions.Updated);
    }
}
