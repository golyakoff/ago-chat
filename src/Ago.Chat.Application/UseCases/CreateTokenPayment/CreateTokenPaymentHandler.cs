using Ago.Chat.Application.Abstractions;
using Ago.Chat.Domain;
using Ago.Platform.Kernel;

namespace Ago.Chat.Application.UseCases.CreateTokenPayment;

/// <summary>
/// `26-291`: `CreateCheckoutSessionHandler`'s own near-clone for the YooKassa Android SDK's
/// tokenization flow (`docs/backlog/26-289-yookassa-android-sdk.md`) - the identical seat-count
/// validation, the identical fresh `IPriceCatalogRepository` read (`CLAUDE.md` rule 8, `25-43`'s own
/// discipline), and the identical <see cref="BillingSubscription.Create"/>`(Pending)` write. It differs
/// in exactly two respects, both already called out in the design doc: it takes a
/// <see cref="CreateTokenPayment.PaymentToken"/> instead of relying on a configured return URL, and it
/// calls <see cref="IYooKassaPaymentsClient.CreatePaymentWithTokenAsync"/> instead of
/// <see cref="IYooKassaPaymentsClient.CreatePaymentAsync"/>.
///
/// <para><b>A sibling handler, not a branch inside <c>CreateCheckoutSessionHandler</c>.</b> The design
/// doc's own rejected alternative - "branch inside `CreateCheckoutSessionHandler` on 'token present ->
/// token payment, else redirect'" - would make one handler carry two payment shapes and two response
/// shapes (redirect-only vs. status+optional-url), and that handler's own integration tests already
/// assert the redirect contract. `CLAUDE.md` rule 15 ("one ticket, one thing") is the same reasoning
/// applied one layer down: one handler, one promise - everything the two must not diverge on
/// (<see cref="SubscriptionTierBands"/>, the price read, the <see cref="BillingSubscription"/> write)
/// is shared through the port and the Domain type, not copy-pasted.</para>
///
/// <para><b>No <see cref="CreateCheckoutSession.BillingOptions"/> dependency, deliberately.</b> The
/// redirect flow's `CheckoutReturnUrl` exists only because ЮKassa's hosted page needs somewhere to send
/// a browser back to; the SDK flow's own confirmation step (when one is needed) runs inside the app's
/// own `createConfirmationIntent`, never a browser redirect, so there is no return URL for this handler
/// to configure or read.</para>
/// </summary>
public sealed class CreateTokenPaymentHandler(
    ISiteRepository sites,
    IPermissionChecker permissions,
    IBillingSubscriptionRepository subscriptions,
    IYooKassaPaymentsClient yooKassa,
    IPriceCatalogRepository prices,
    IIdGenerator idGenerator,
    IClock clock)
{
    public async Task<Result<CreateTokenPaymentResult>> HandleAsync(CreateTokenPayment command, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(command.PaymentToken))
        {
            return ConversationErrors.BillingInvalidPaymentToken("A payment token is required.");
        }

        var allowed = await permissions.HasPermissionAsync(
            command.RequestedBy, command.SiteId, Permission.SiteConfigure, cancellationToken);
        if (!allowed)
        {
            return ConversationErrors.Forbidden("Operator does not have permission to configure this site's billing.");
        }

        var site = await sites.GetByIdAsync(command.SiteId, cancellationToken);
        if (site is null)
        {
            return ConversationErrors.SiteNotFound(command.SiteId.Value);
        }

        if (!SubscriptionTierBands.TryResolveTier(command.RequestedSeats, out var tier))
        {
            return ConversationErrors.BillingInvalidSeatCount(
                $"{command.RequestedSeats} seats is not a purchasable seat count - expected "
                + $"{SubscriptionTierBands.MinSeats}-{SubscriptionTierBands.MaxSeats}.");
        }

        // `25-43`: the identical "read both seat-pricing keys' own currently-effective versions, at the
        // moment of the charge, never cached" discipline CreateCheckoutSessionHandler's own remarks
        // describe in full - restated here rather than re-explained, since the two handlers share the
        // exact same reasoning for the exact same two keys.
        var basePrice = await prices.FindCurrentAsync(SubscriptionTierBands.BaseSeatPriceKey, cancellationToken);
        if (basePrice is null)
        {
            return PriceCatalogErrors.PriceNotConfigured(SubscriptionTierBands.BaseSeatPriceKey.Value);
        }

        var extraPrice = await prices.FindCurrentAsync(SubscriptionTierBands.ExtraSeatPriceKey, cancellationToken);
        if (extraPrice is null)
        {
            return PriceCatalogErrors.PriceNotConfigured(SubscriptionTierBands.ExtraSeatPriceKey.Value);
        }

        var now = clock.UtcNow;
        var amount = SubscriptionTierBands.ComputeSeatPriceRub(command.RequestedSeats, basePrice.AmountRub, extraPrice.AmountRub);
        var idempotenceKey = idGenerator.NewId(now).ToString();

        var paymentResult = await yooKassa.CreatePaymentWithTokenAsync(
            new CreatePaymentWithTokenRequest(
                amount,
                $"AGO Chat - {tier} tier, {command.RequestedSeats} seats",
                command.PaymentToken,
                idempotenceKey),
            cancellationToken);

        if (paymentResult is CreatePaymentWithTokenResult.Refused refused)
        {
            return ConversationErrors.BillingPaymentProviderRefused(refused.Reason);
        }

        var success = (CreatePaymentWithTokenResult.Success)paymentResult;
        var subscriptionId = new BillingSubscriptionId(idGenerator.NewId(now));
        // `13-02`'s own "never the redirect alone" discipline, restated for the token flow: this row is
        // created Pending regardless of what status ЮKassa's own reply reports right now (even an
        // immediately-`succeeded` token payment) - only `ProcessYooKassaWebhookHandler`'s own re-query
        // (`adr/0190`, unchanged by this item) ever moves it to Succeeded and grants anything.
        var subscription = BillingSubscription.Create(
            subscriptionId, command.SiteId, success.PaymentId, command.RequestedSeats, tier,
            basePrice.Sequence, extraPrice.Sequence, now);
        await subscriptions.SaveAsync(subscription, cancellationToken);

        return new CreateTokenPaymentResult(success.Status, success.ConfirmationUrl);
    }
}
