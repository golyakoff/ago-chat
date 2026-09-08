using Ago.Chat.Application.Tests.Fakes;
using Ago.Chat.Application.UseCases.CreateCheckoutSession;
using Ago.Chat.Application.UseCases.ProcessSubscriptionRenewal;
using Ago.Chat.Domain;

namespace Ago.Chat.Application.Tests.UseCases.ProcessSubscriptionRenewal;

/// <summary>`23-86`: this handler's own recurring-charge amount computation is base-seat-priced only
/// (`PricePerSeatRub * RequestedSeats`) - meaningless, and silently wrong (Rub 0), for an option
/// subscription, since an option is priced flat and this item's own Scope forbids inventing that price
/// ("no price, anywhere"). Proven here that the handler refuses loudly instead of computing a wrong
/// charge - the one case this handler's own remarks name but nothing before this item exercised, since
/// no production code path could hand it an option row until this item added
/// <see cref="BillingSubscription.OptionKey"/>.</summary>
public class ProcessSubscriptionRenewalHandlerTests
{
    private static readonly SiteId SiteId = new(Guid.NewGuid());
    private static readonly DateTimeOffset Now = new(2026, 9, 8, 12, 0, 0, TimeSpan.Zero);
    private static readonly BillingOptions Billing = new() { PricePerSeatRub = 500m, CheckoutReturnUrl = "https://console.example/return" };

    [Fact]
    public async Task HandleAsync_WhenTheDueSubscriptionIsAnOption_ThrowsRatherThanChargingAWrongOrZeroAmount()
    {
        var optionId = new BillingSubscriptionId(Guid.NewGuid());
        var option = BillingSubscription.CreateOption(optionId, SiteId, "pmt_option", new BillingOptionKey("channel-telegram"), Now - BillingSubscription.PeriodLength);
        option.MarkSucceeded("card_on_file", Now - BillingSubscription.PeriodLength, alignedPeriodEnd: Now);

        var subscriptions = new FakeBillingSubscriptionRepository();
        subscriptions.Seed(option);
        var yooKassa = new FakeYooKassaPaymentsClient();
        var applier = new FakeSubscriptionRenewalApplier();
        var handler = new ProcessSubscriptionRenewalHandler(subscriptions, yooKassa, Billing, applier, new FakeClock(Now));

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
