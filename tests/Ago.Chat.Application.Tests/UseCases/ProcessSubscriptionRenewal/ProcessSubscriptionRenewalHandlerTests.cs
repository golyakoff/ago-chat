using Ago.Chat.Application.Tests.Fakes;
using Ago.Chat.Application.UseCases.CreateCheckoutSession;
using Ago.Chat.Application.UseCases.ProcessSubscriptionRenewal;
using Ago.Chat.Domain;

namespace Ago.Chat.Application.Tests.UseCases.ProcessSubscriptionRenewal;

/// <summary>`23-86`: this handler's own recurring-charge amount computation is base-seat-priced only
/// (`SubscriptionTierBands.ComputeSeatPriceRub` against `RequestedSeats`) - meaningless, and silently
/// wrong, for an option subscription, since an option is priced flat and this item's own Scope forbids
/// inventing that price ("no price, anywhere"). The handler guards this with an explicit
/// `IsOption` check that throws before the amount is ever computed, so the specific wrong number the
/// formula would produce for `RequestedSeats == 0` (`25-29`'s own base-plus-marginal formula no longer
/// gives Rub 0 the way `0008`'s superseded flat rate did - it gives the base seat price instead) never
/// actually matters; what this test proves is that the guard fires first, not what the formula would
/// have returned. The one case this handler's own remarks name but nothing before this item exercised,
/// since no production code path could hand it an option row until this item added
/// <see cref="BillingSubscription.OptionKey"/>.</summary>
public class ProcessSubscriptionRenewalHandlerTests
{
    private static readonly SiteId SiteId = new(Guid.NewGuid());
    private static readonly DateTimeOffset Now = new(2026, 9, 8, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task HandleAsync_WhenTheDueSubscriptionIsAnOption_ThrowsRatherThanChargingAWrongOrZeroAmount()
    {
        var optionId = new BillingSubscriptionId(Guid.NewGuid());
        var option = BillingSubscription.CreateOption(optionId, SiteId, "pmt_option", new BillingOptionKey("channel-telegram"), Now - BillingSubscription.PeriodLength);
        option.MarkSucceeded("card_on_file", Now - BillingSubscription.PeriodLength, alignedPeriodEnd: Now);

        var subscriptions = new FakeBillingSubscriptionRepository();
        subscriptions.Seed(option);
        var yooKassa = new FakeYooKassaPaymentsClient();
        // `25-43`: never seeded - the IsOption guard this test proves fires before either seat-pricing
        // key is ever read, so an empty catalog is the honest fixture, not an oversight.
        var prices = new FakePriceCatalogRepository();
        var applier = new FakeSubscriptionRenewalApplier();
        var handler = new ProcessSubscriptionRenewalHandler(subscriptions, yooKassa, prices, applier, new FakeClock(Now));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => handler.HandleAsync(new Application.UseCases.ProcessSubscriptionRenewal.ProcessSubscriptionRenewal(optionId), CancellationToken.None));

        // No charge was ever attempted, and the applier was never told any outcome - the refusal
        // happens before either, so a misconfigured deployment cannot silently charge Rub 0 or leave a
        // half-applied outcome behind.
        Assert.Null(yooKassa.LastChargeRequest);
        Assert.Empty(applier.RenewedSuccessfully);
        Assert.Empty(applier.RenewalFailures);
        Assert.Empty(applier.Lapsed);
    }
}
