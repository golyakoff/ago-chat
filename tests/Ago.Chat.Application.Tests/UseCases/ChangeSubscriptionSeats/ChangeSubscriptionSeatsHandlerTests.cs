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
        FakeSeatChangeApplier Applier);

    private static Fixture CreateFixture()
    {
        var subscriptions = new FakeBillingSubscriptionRepository();
        var permissions = new FakePermissionChecker();
        permissions.Grant(OperatorId, SiteId, Permission.SiteConfigure);
        var yooKassa = new FakeYooKassaPaymentsClient();
        var applier = new FakeSeatChangeApplier();

        var handler = new Application.UseCases.ChangeSubscriptionSeats.ChangeSubscriptionSeatsHandler(
            subscriptions, permissions, yooKassa, applier,
            new BillingOptions { PricePerSeatRub = 500m, CheckoutReturnUrl = "https://console.example/return" },
            new FakeIdGenerator(), new FakeClock(Now));

        return new Fixture(handler, subscriptions, yooKassa, applier);
    }

    private static BillingSubscription SeedSucceeded(
        FakeBillingSubscriptionRepository subscriptions, BillingSubscriptionId id, int seats, string tier, DateTimeOffset? succeededAt = null)
    {
        var when = succeededAt ?? Now - BillingSubscription.PeriodLength;
        var subscription = BillingSubscription.Create(id, SiteId, "pmt_123", seats, tier, when);
        subscription.MarkSucceeded("card_abc", when);
        subscriptions.Seed(subscription);
        return subscription;
    }

    [Fact]
    public async Task HandleAsync_WhenSeatCountIsUnchanged_ReturnsError()
    {
        var fixture = CreateFixture();
        var id = new BillingSubscriptionId(Guid.NewGuid());
        SeedSucceeded(fixture.Subscriptions, id, seats: 5, tier: SubscriptionTierBands.Starter);

        var result = await fixture.Handler.HandleAsync(
            new Application.UseCases.ChangeSubscriptionSeats.ChangeSubscriptionSeats(OperatorId, SiteId, id, 5), CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Billing.SeatCountUnchanged", result.Error!.Value.Code);
    }

    [Fact]
    public async Task HandleAsync_WhenAnUpgrade_ChargesTheProratedDifference_AndAppliesImmediately()
    {
        var fixture = CreateFixture();
        var id = new BillingSubscriptionId(Guid.NewGuid());
        // Succeeded half a period ago, so CurrentPeriodEnd sits exactly half a period into the future
        // from Now - a clean, checkable remaining-days fraction (0.5) computed honestly through
        // MarkSucceeded's own "CurrentPeriodEnd = succeededAt + PeriodLength" rule, not forced.
        var halfPeriodAgo = Now - TimeSpan.FromDays(BillingSubscription.PeriodLength.TotalDays / 2);
        SeedSucceeded(fixture.Subscriptions, id, seats: 5, tier: SubscriptionTierBands.Starter, succeededAt: halfPeriodAgo);

        var result = await fixture.Handler.HandleAsync(
            new Application.UseCases.ChangeSubscriptionSeats.ChangeSubscriptionSeats(OperatorId, SiteId, id, 15), CancellationToken.None);

        Assert.True(result.IsSuccess);
        var upgraded = Assert.IsType<ChangeSubscriptionSeatsResult.Upgraded>(result.Value);
        // (15*500 - 5*500) * 0.5 = 2500.00
        Assert.Equal(2500.00m, upgraded.ProratedAmountRub);
        Assert.NotNull(fixture.YooKassa.LastChargeRequest);
        Assert.Equal(2500.00m, fixture.YooKassa.LastChargeRequest!.AmountRub);
        Assert.Single(fixture.Applier.Applied);
        Assert.Equal(15, fixture.Applier.Applied[0].NewSeatCount);
    }

    [Fact]
    public async Task HandleAsync_WhenAnUpgradeIsRefused_ReturnsError_AndAppliesNothing()
    {
        var fixture = CreateFixture();
        var id = new BillingSubscriptionId(Guid.NewGuid());
        SeedSucceeded(fixture.Subscriptions, id, seats: 5, tier: SubscriptionTierBands.Starter);
        fixture.YooKassa.ChargeResult = new ChargeStoredPaymentMethodResult.Refused("card declined");

        var result = await fixture.Handler.HandleAsync(
            new Application.UseCases.ChangeSubscriptionSeats.ChangeSubscriptionSeats(OperatorId, SiteId, id, 15), CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Billing.PaymentProviderRefused", result.Error!.Value.Code);
        Assert.Empty(fixture.Applier.Applied);
    }

    [Fact]
    public async Task HandleAsync_WhenADowngrade_SchedulesItWithNoChargeAndNoImmediateApply()
    {
        var fixture = CreateFixture();
        var id = new BillingSubscriptionId(Guid.NewGuid());
        var subscription = SeedSucceeded(fixture.Subscriptions, id, seats: 20, tier: SubscriptionTierBands.Growth);

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
