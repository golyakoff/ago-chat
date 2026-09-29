using Ago.Chat.Application.Abstractions;
using Ago.Chat.Application.Tests.Fakes;
using Ago.Chat.Application.UseCases.RenewNow;
using Ago.Chat.Domain;

namespace Ago.Chat.Application.Tests.UseCases.RenewNow;

/// <summary>`26-296`: pay-early, proven at the handler level against
/// <see cref="FakeSubscriptionRenewalApplier"/> - the identical "fake the applier, prove the handler
/// called it correctly" shape <c>ProcessSubscriptionRenewalHandlerTests</c> already establishes for the
/// automatic sweep's own applier calls.</summary>
public class RenewNowHandlerTests
{
    private static readonly SiteId SiteId = new(Guid.NewGuid());
    private static readonly OperatorId OperatorId = new(Guid.NewGuid());
    private static readonly DateTimeOffset Now = new(2026, 9, 29, 12, 0, 0, TimeSpan.Zero);

    private sealed record Fixture(
        RenewNowHandler Handler, FakeBillingSubscriptionRepository Subscriptions, FakeYooKassaPaymentsClient YooKassa,
        FakeSubscriptionRenewalApplier Applier, FakePriceCatalogRepository Prices);

    private static Fixture CreateFixture(bool grantPermission = true)
    {
        var permissions = new FakePermissionChecker();
        if (grantPermission)
        {
            permissions.Grant(OperatorId, SiteId, Permission.SiteConfigure);
        }

        var subscriptions = new FakeBillingSubscriptionRepository();
        var yooKassa = new FakeYooKassaPaymentsClient();
        var prices = new FakePriceCatalogRepository();
        prices.SeedVersion(SubscriptionTierBands.BaseSeatPriceKey, 490m, Now);
        prices.SeedVersion(SubscriptionTierBands.ExtraSeatPriceKey, 200m, Now);
        var applier = new FakeSubscriptionRenewalApplier();

        var handler = new RenewNowHandler(subscriptions, permissions, yooKassa, prices, applier, new FakeClock(Now));
        return new Fixture(handler, subscriptions, yooKassa, applier, prices);
    }

    private static BillingSubscription SeedSucceededBase(Fixture fixture, int requestedSeats = 5, DateTimeOffset? periodEndsAt = null)
    {
        var subscription = BillingSubscription.Create(
            new BillingSubscriptionId(Guid.NewGuid()), SiteId, "pmt_base", requestedSeats, SubscriptionTierBands.Starter,
            baseSeatPriceVersion: 1, extraSeatPriceVersion: 1, createdAt: Now - TimeSpan.FromDays(20));
        subscription.MarkSucceeded("card_on_file", Now - TimeSpan.FromDays(20));
        fixture.Subscriptions.Seed(subscription);
        return subscription;
    }

    [Fact]
    public async Task HandleAsync_ForASucceededBaseSubscription_ChargesTheRecurringAmount_AndExtendsThePeriodByOnePeriod()
    {
        var fixture = CreateFixture();
        var subscription = SeedSucceededBase(fixture, requestedSeats: 5);
        var expectedNewPeriodEnd = subscription.CurrentPeriodEnd!.Value + BillingSubscription.PeriodLength;

        var result = await fixture.Handler.HandleAsync(
            new Application.UseCases.RenewNow.RenewNow(OperatorId, SiteId, subscription.Id), CancellationToken.None);

        Assert.True(result.IsSuccess, result.IsFailure ? result.Error!.Value.Message : null);
        // 5 seats: base (490) + 2 extra seats × 200 = 890, the identical banded formula every other
        // charge site in this codebase uses.
        Assert.Equal(890m, result.Value.AmountRub);
        Assert.Equal(expectedNewPeriodEnd, result.Value.NewPeriodEnd);
        Assert.Equal(890m, fixture.YooKassa.LastChargeRequest!.AmountRub);
        Assert.Equal("card_on_file", fixture.YooKassa.LastChargeRequest.PaymentMethodId);
        Assert.Equal($"renewal:{subscription.Id.Value}:{Now:yyyy-MM-dd}", fixture.YooKassa.LastChargeRequest.IdempotenceKey);
        var renewed = Assert.Single(fixture.Applier.RenewedSuccessfully);
        Assert.Equal(subscription.Id, renewed);
    }

    [Fact]
    public async Task HandleAsync_IncludesPurchasedExtraAdministrators_InTheChargedAmount()
    {
        var fixture = CreateFixture();
        var subscription = SeedSucceededBase(fixture, requestedSeats: 3);
        subscription.ApplyAdministratorPurchase(2, adminExtraPriceVersion: 1);
        fixture.Prices.SeedVersion(SubscriptionTierBands.AdminExtraPriceKey, 500m, Now);

        var result = await fixture.Handler.HandleAsync(
            new Application.UseCases.RenewNow.RenewNow(OperatorId, SiteId, subscription.Id), CancellationToken.None);

        Assert.True(result.IsSuccess, result.IsFailure ? result.Error!.Value.Message : null);
        // 3 seats = base only (490) + 2 extra Administrators × 500 = 1490.
        Assert.Equal(1490m, result.Value.AmountRub);
    }

    [Fact]
    public async Task HandleAsync_UsesTheSameIdempotenceKeyShapeAsTheAutomaticSweep_SoADoubleFireCannotDoubleCharge()
    {
        var fixture = CreateFixture();
        var subscription = SeedSucceededBase(fixture);

        await fixture.Handler.HandleAsync(new Application.UseCases.RenewNow.RenewNow(OperatorId, SiteId, subscription.Id), CancellationToken.None);

        // `13-03`'s own deterministic renewal:{id}:{date} format - ЮKassa itself would dedupe a same-day
        // automatic-sweep attempt against this exact key.
        Assert.Equal($"renewal:{subscription.Id.Value}:{Now:yyyy-MM-dd}", fixture.YooKassa.LastChargeRequest!.IdempotenceKey);
    }

    [Fact]
    public async Task HandleAsync_WhenAlreadyRenewedToday_RefusesOutright_AndNeverCallsYooKassaAgain()
    {
        var fixture = CreateFixture();
        var subscription = SeedSucceededBase(fixture);

        var first = await fixture.Handler.HandleAsync(
            new Application.UseCases.RenewNow.RenewNow(OperatorId, SiteId, subscription.Id), CancellationToken.None);
        Assert.True(first.IsSuccess);

        // The real ISubscriptionRenewalApplier (unlike the fake used here) reloads and mutates the row
        // inside its own transaction, so LastRenewalAttemptAt would already carry today's date by the
        // time a second HandleAsync call re-reads it - simulated here by calling the identical Domain
        // method the real applier calls, against this test's own seeded row.
        subscription.RecordRenewalSuccess(Now, "card_on_file", 1, 1);
        var chargeCountAfterFirstCall = fixture.YooKassa.LastChargeRequest;

        var second = await fixture.Handler.HandleAsync(
            new Application.UseCases.RenewNow.RenewNow(OperatorId, SiteId, subscription.Id), CancellationToken.None);

        Assert.True(second.IsFailure);
        Assert.Equal("Billing.SubscriptionAlreadyRenewedToday", second.Error!.Value.Code);
        // The second call never reached ЮKassa at all - LastChargeRequest is unchanged from the first call.
        Assert.Same(chargeCountAfterFirstCall, fixture.YooKassa.LastChargeRequest);
    }

    [Fact]
    public async Task HandleAsync_WhenYooKassaRefusesTheCharge_ReturnsPaymentProviderRefused_AndDoesNotMarkTheSubscriptionPastDue()
    {
        var fixture = CreateFixture();
        var subscription = SeedSucceededBase(fixture);
        fixture.YooKassa.ChargeResult = new ChargeStoredPaymentMethodResult.Refused("card declined");

        var result = await fixture.Handler.HandleAsync(
            new Application.UseCases.RenewNow.RenewNow(OperatorId, SiteId, subscription.Id), CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Billing.PaymentProviderRefused", result.Error!.Value.Code);
        // `26-296`'s own deliberate divergence from the automatic sweep: nothing was actually due, so a
        // refused optional early payment must not push this still-fully-paid subscription into PastDue.
        Assert.Empty(fixture.Applier.RenewalFailures);
        Assert.Empty(fixture.Applier.RenewedSuccessfully);
        Assert.Equal(BillingSubscriptionStatus.Succeeded, subscription.Status);
    }

    [Fact]
    public async Task HandleAsync_ForAnOptionSubscription_ReturnsSubscriptionNotActive_AndNeverCallsYooKassa()
    {
        var fixture = CreateFixture();
        var option = BillingSubscription.CreateOption(
            new BillingSubscriptionId(Guid.NewGuid()), SiteId, "pmt_option", new BillingOptionKey("channel-telegram"), Now - TimeSpan.FromDays(10));
        option.MarkSucceeded("card_on_file", Now - TimeSpan.FromDays(10), alignedPeriodEnd: Now + TimeSpan.FromDays(20));
        fixture.Subscriptions.Seed(option);

        var result = await fixture.Handler.HandleAsync(
            new Application.UseCases.RenewNow.RenewNow(OperatorId, SiteId, option.Id), CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Billing.SubscriptionNotActive", result.Error!.Value.Code);
        Assert.Null(fixture.YooKassa.LastChargeRequest);
    }

    [Theory]
    [InlineData("Pending")]
    [InlineData("Failed")]
    public async Task HandleAsync_ForASubscriptionThatIsNotSucceeded_ReturnsSubscriptionNotActive(string status)
    {
        var fixture = CreateFixture();
        var subscription = BillingSubscription.Create(
            new BillingSubscriptionId(Guid.NewGuid()), SiteId, "pmt_base", 5, SubscriptionTierBands.Starter,
            baseSeatPriceVersion: 1, extraSeatPriceVersion: 1, createdAt: Now);
        if (status == "Failed")
        {
            subscription.MarkFailed();
        }

        fixture.Subscriptions.Seed(subscription);

        var result = await fixture.Handler.HandleAsync(
            new Application.UseCases.RenewNow.RenewNow(OperatorId, SiteId, subscription.Id), CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Billing.SubscriptionNotActive", result.Error!.Value.Code);
        Assert.Null(fixture.YooKassa.LastChargeRequest);
    }

    [Fact]
    public async Task HandleAsync_WhenTheOperatorLacksSiteConfigure_ReturnsForbidden()
    {
        var fixture = CreateFixture(grantPermission: false);
        var subscription = SeedSucceededBase(fixture);

        var result = await fixture.Handler.HandleAsync(
            new Application.UseCases.RenewNow.RenewNow(OperatorId, SiteId, subscription.Id), CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Conversation.Forbidden", result.Error!.Value.Code);
        Assert.Null(fixture.YooKassa.LastChargeRequest);
    }

    [Fact]
    public async Task HandleAsync_WhenTheSubscriptionDoesNotExistForThisSite_ReturnsSubscriptionNotFound()
    {
        var fixture = CreateFixture();
        var missingId = new BillingSubscriptionId(Guid.NewGuid());

        var result = await fixture.Handler.HandleAsync(
            new Application.UseCases.RenewNow.RenewNow(OperatorId, SiteId, missingId), CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Billing.SubscriptionNotFound", result.Error!.Value.Code);
    }
}
