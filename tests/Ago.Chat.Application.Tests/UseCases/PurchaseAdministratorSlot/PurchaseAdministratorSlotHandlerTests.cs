using Ago.Chat.Application.Tests.Fakes;
using Ago.Chat.Application.UseCases.PurchaseAdministratorSlot;
using Ago.Chat.Domain;

namespace Ago.Chat.Application.Tests.UseCases.PurchaseAdministratorSlot;

public class PurchaseAdministratorSlotHandlerTests
{
    private static readonly SiteId SiteId = new(Guid.NewGuid());
    private static readonly OperatorId OperatorId = new(Guid.NewGuid());
    private static readonly DateTimeOffset Now = new(2026, 9, 10, 12, 0, 0, TimeSpan.Zero);

    private sealed record Fixture(
        PurchaseAdministratorSlotHandler Handler,
        FakeBillingSubscriptionRepository Subscriptions,
        FakeYooKassaPaymentsClient YooKassa,
        FakeAdministratorSlotChangeApplier Applier,
        FakePriceCatalogRepository Prices,
        FakePermissionChecker Permissions);

    // `25-41`: ago-business decision 0012's own real, decided number - +500 rub/mo per extra
    // Administrator, flat, never banded.
    private const decimal AdminExtraPriceRub = 500m;

    private static Fixture CreateFixture(bool seedPrice = true, bool grantPermission = true)
    {
        var subscriptions = new FakeBillingSubscriptionRepository();
        var permissions = new FakePermissionChecker();
        if (grantPermission)
        {
            permissions.Grant(OperatorId, SiteId, Permission.SiteConfigure);
        }

        var yooKassa = new FakeYooKassaPaymentsClient();
        var applier = new FakeAdministratorSlotChangeApplier();
        var prices = new FakePriceCatalogRepository();
        if (seedPrice)
        {
            prices.SeedVersion(SubscriptionTierBands.AdminExtraPriceKey, AdminExtraPriceRub, Now);
        }

        var handler = new PurchaseAdministratorSlotHandler(
            subscriptions, permissions, yooKassa, prices, applier, new FakeIdGenerator(), new FakeClock(Now));

        return new Fixture(handler, subscriptions, yooKassa, applier, prices, permissions);
    }

    private static async Task<BillingSubscription> SeedSucceededAsync(
        Fixture fixture, BillingSubscriptionId id, int extraAdministratorsAlreadyPurchased, DateTimeOffset? succeededAt = null)
    {
        var when = succeededAt ?? Now - BillingSubscription.PeriodLength;
        var subscription = BillingSubscription.Create(
            id, SiteId, "pmt_123", requestedSeats: 5, SubscriptionTierBands.Starter, baseSeatPriceVersion: 1, extraSeatPriceVersion: 1, when);
        subscription.MarkSucceeded("card_abc", when);
        if (extraAdministratorsAlreadyPurchased > 0)
        {
            var currentPrice = await fixture.Prices.FindCurrentAsync(SubscriptionTierBands.AdminExtraPriceKey, CancellationToken.None);
            subscription.ApplyAdministratorPurchase(extraAdministratorsAlreadyPurchased, currentPrice!.Sequence);
        }

        fixture.Subscriptions.Seed(subscription);
        return subscription;
    }

    // `25-41`'s own Done-when: the charge matches 0012's own +500 rub/mo per additional Administrator -
    // the first purchase, prorated for half the billing period, so the full monthly rate is halved.
    [Fact]
    public async Task HandleAsync_WhenTheFirstPurchase_ChargesTheFullFlatRate_Prorated()
    {
        var fixture = CreateFixture();
        var id = new BillingSubscriptionId(Guid.NewGuid());
        var halfPeriodAgo = Now - TimeSpan.FromDays(BillingSubscription.PeriodLength.TotalDays / 2);
        await SeedSucceededAsync(fixture, id, extraAdministratorsAlreadyPurchased: 0, succeededAt: halfPeriodAgo);

        var result = await fixture.Handler.HandleAsync(
            new Application.UseCases.PurchaseAdministratorSlot.PurchaseAdministratorSlot(OperatorId, SiteId, id, RequestedExtraAdministrators: 1),
            CancellationToken.None);

        Assert.True(result.IsSuccess, result.IsFailure ? result.Error!.Value.Message : null);
        // old (0 extra, nothing bought before) = 0; new (1 extra) = 500; (500 - 0) x 0.5 = 250.00 -
        // never SubscriptionTierBands.ComputeSeatPriceRub's own banded shape, which does not apply here.
        Assert.Equal(250.00m, result.Value.ProratedAmountRub);
        Assert.Equal(1, result.Value.NewExtraAdministratorCount);
        Assert.NotNull(fixture.YooKassa.LastChargeRequest);
        Assert.Equal(250.00m, fixture.YooKassa.LastChargeRequest!.AmountRub);
        Assert.Single(fixture.Applier.Applied);
        Assert.Equal(1, fixture.Applier.Applied[0].NewExtraAdministratorCount);
    }

    // `25-43`'s own correctness discipline restated for this handler: the "old" side of the proration
    // reads the subscription's own stored price version, never the catalog's currently-effective one -
    // proven here by seeding a real historical version that differs from a later price change would.
    [Fact]
    public async Task HandleAsync_WhenBuyingASecondExtraAdministrator_ChargesOnlyTheDifference()
    {
        var fixture = CreateFixture();
        var id = new BillingSubscriptionId(Guid.NewGuid());
        var halfPeriodAgo = Now - TimeSpan.FromDays(BillingSubscription.PeriodLength.TotalDays / 2);
        await SeedSucceededAsync(fixture, id, extraAdministratorsAlreadyPurchased: 1, succeededAt: halfPeriodAgo);

        var result = await fixture.Handler.HandleAsync(
            new Application.UseCases.PurchaseAdministratorSlot.PurchaseAdministratorSlot(OperatorId, SiteId, id, RequestedExtraAdministrators: 2),
            CancellationToken.None);

        Assert.True(result.IsSuccess, result.IsFailure ? result.Error!.Value.Message : null);
        // old (1 extra already paid for) = 500; new (2 extra) = 1000; (1000 - 500) x 0.5 = 250.00 -
        // the marginal cost of the second slot alone, not the full 1000.
        Assert.Equal(250.00m, result.Value.ProratedAmountRub);
    }

    [Theory]
    [InlineData(1)] // exactly the current count - not an increase
    [InlineData(0)] // a decrease - this endpoint only ever increases
    public async Task HandleAsync_WhenTheRequestedCountIsNotAnIncrease_ReturnsError(int requested)
    {
        var fixture = CreateFixture();
        var id = new BillingSubscriptionId(Guid.NewGuid());
        await SeedSucceededAsync(fixture, id, extraAdministratorsAlreadyPurchased: 1);

        var result = await fixture.Handler.HandleAsync(
            new Application.UseCases.PurchaseAdministratorSlot.PurchaseAdministratorSlot(OperatorId, SiteId, id, requested),
            CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Billing.AdministratorCountNotAnIncrease", result.Error!.Value.Code);
        Assert.Null(fixture.YooKassa.LastChargeRequest);
        Assert.Empty(fixture.Applier.Applied);
    }

    // `25-41`'s own Scope: the real Rouble figure is ago-business's own catalog entry, never hardcoded
    // here - a key with nothing published refuses cleanly, the identical "built, not yet for sale"
    // discipline 25-43 already proves for seats, never a crash and never a zero-amount charge.
    [Fact]
    public async Task HandleAsync_WhenNoPriceIsConfiguredForTheAdminExtraKey_ReturnsPriceNotConfigured()
    {
        var fixture = CreateFixture(seedPrice: false);
        var id = new BillingSubscriptionId(Guid.NewGuid());
        await SeedSucceededAsync(fixture, id, extraAdministratorsAlreadyPurchased: 0);

        var result = await fixture.Handler.HandleAsync(
            new Application.UseCases.PurchaseAdministratorSlot.PurchaseAdministratorSlot(OperatorId, SiteId, id, RequestedExtraAdministrators: 1),
            CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Billing.PriceNotConfigured", result.Error!.Value.Code);
        Assert.Null(fixture.YooKassa.LastChargeRequest);
        Assert.Empty(fixture.Applier.Applied);
    }

    [Fact]
    public async Task HandleAsync_WhenCallerLacksSiteConfigurePermission_ReturnsForbidden()
    {
        var fixture = CreateFixture(grantPermission: false);
        var id = new BillingSubscriptionId(Guid.NewGuid());
        await SeedSucceededAsync(fixture, id, extraAdministratorsAlreadyPurchased: 0);

        var result = await fixture.Handler.HandleAsync(
            new Application.UseCases.PurchaseAdministratorSlot.PurchaseAdministratorSlot(OperatorId, SiteId, id, RequestedExtraAdministrators: 1),
            CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Conversation.Forbidden", result.Error!.Value.Code);
        Assert.Null(fixture.YooKassa.LastChargeRequest);
    }
}
