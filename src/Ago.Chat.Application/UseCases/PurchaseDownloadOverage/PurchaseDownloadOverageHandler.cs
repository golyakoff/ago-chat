using Ago.Chat.Application.Abstractions;
using Ago.Chat.Application.UseCases.CreateCheckoutSession;
using Ago.Chat.Domain;
using Ago.Platform.Kernel;

namespace Ago.Chat.Application.UseCases.PurchaseDownloadOverage;

/// <summary>
/// `25-84`: a metered purchase - the first in this codebase whose amount is not chosen from a menu.
///
/// <para><b>What reading the existing checkout mechanism actually found, since this item's own text
/// warned it might not accept a variable amount: it already does.</b>
/// <see cref="IYooKassaPaymentsClient.CreatePaymentAsync"/> takes a plain
/// <see cref="CreatePaymentRequest.AmountRub"/> <see langword="decimal"/>, and
/// <c>PurchaseAdministratorSlotHandler</c> already passes one computed at the moment of payment (a
/// proration against the subscription's own remaining days). The fixed-price shape lives in the
/// *callers* - <c>CreateCheckoutSessionHandler</c> resolving a seat band - never in the port or the
/// adapter. So this handler needed no change to the payment mechanism at all: it computes its own
/// amount and calls the same port, which is what "reuse the mechanism, do not invent a new payment
/// flow" was asking for.</para>
///
/// <para><b>The hosted-checkout path (<see cref="IYooKassaPaymentsClient.CreatePaymentAsync"/>), not
/// the charge-on-file one <c>PurchaseAdministratorSlotHandler</c> uses.</b> A charge on file needs a
/// <see cref="BillingSubscription.PaymentMethodId"/>, which only a site that has already completed a
/// paid checkout has - and the tenants most likely to be blocked here are exactly the ones on the free
/// tier, whose thresholds are the lowest and who have no stored payment method at all. A path that
/// only works for paying customers is not an escape hatch for a blocked account. The cost is one
/// redirect and a webhook round trip instead of an in-request charge, which is the same shape every
/// first purchase in this product already takes.</para>
///
/// <para><b>Nothing here unblocks anybody.</b> This handler creates a
/// <see cref="DownloadOverageChargeStatus.Pending"/> row and hands back a URL; the tenant is still
/// blocked until <c>BillingWebhookApplier</c> promotes that row on a verified `payment.succeeded`.
/// That is `13-02`'s own "never the redirect alone" applied to a second kind of purchase - and it is
/// the reason <c>SiteAttachmentStorageHandlersTests</c> proves the unblock by driving the real webhook
/// applier, not by asserting that this method returned a URL.</para>
/// </summary>
public sealed class PurchaseDownloadOverageHandler(
    ISiteRepository sites,
    IPermissionChecker permissions,
    IAttachmentEgressReadStore egressReads,
    IDownloadThresholdReadStore thresholds,
    IDownloadOverageReadStore overageReads,
    IDownloadOverageChargeRepository charges,
    IPriceCatalogRepository prices,
    IYooKassaPaymentsClient yooKassa,
    BillingOptions billingOptions,
    IIdGenerator idGenerator,
    IClock clock)
{
    public async Task<Result<DownloadOverageCheckoutDto>> HandleAsync(
        PurchaseDownloadOverage command, CancellationToken cancellationToken)
    {
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

        var now = clock.UtcNow;
        var periodMonth = new DateOnly(now.Year, now.Month, 1);
        var egress = await egressReads.GetForSiteAsync(command.SiteId, periodMonth, cancellationToken);
        var tierThresholds = await thresholds.GetForTierAsync(site.Tier, cancellationToken);

        var price = await prices.FindCurrentAsync(DownloadOveragePricing.OveragePerGigabyteKey, cancellationToken);
        if (price is null)
        {
            // `25-43`'s own second decision, reached through this route instead of a charge site: a key
            // nobody has published is "not for sale yet", refused cleanly before any outbound call.
            return PriceCatalogErrors.PriceNotConfigured(DownloadOveragePricing.OveragePerGigabyteKey.Value);
        }

        var settlement = await overageReads.GetSettlementAsync(command.SiteId, periodMonth, cancellationToken);
        var bytesOver = egress.BytesOut - tierThresholds.HardThresholdBytes;
        var outstandingBytes = Math.Max(0L, bytesOver - settlement.SettledBytes);
        var amount = DownloadOveragePricing.ComputeOverageRub(outstandingBytes, price.AmountRub);

        if (amount <= 0m)
        {
            // Two situations, one honest answer: the tenant is not over the threshold at all, or every
            // byte they are over by has already been charged for. Both mean there is nothing to buy,
            // and neither is a server fault - refused as a `400`-shaped domain error rather than a
            // ЮKassa payment for `0 ₽`, which the provider would refuse anyway with a far worse
            // message. A sub-kopeck overage lands here too, deliberately: rounding it up to a kopeck
            // would be inventing a charge, and rounding it to zero is the honest reading.
            return ConversationErrors.DownloadOverageNothingToPay(command.SiteId.Value);
        }

        var idempotenceKey = idGenerator.NewId(now).ToString();
        var description =
            $"AGO Chat - attachment download overage, {outstandingBytes} bytes over ({periodMonth:yyyy-MM})";

        var paymentResult = await yooKassa.CreatePaymentAsync(
            new CreatePaymentRequest(amount, description, billingOptions.CheckoutReturnUrl, idempotenceKey),
            cancellationToken);

        if (paymentResult is CreatePaymentResult.Refused refused)
        {
            return ConversationErrors.BillingPaymentProviderRefused(refused.Reason);
        }

        var success = (CreatePaymentResult.Success)paymentResult;
        await charges.SaveAsync(
            DownloadOverageCharge.PendingCheckout(
                new DownloadOverageChargeId(idGenerator.NewId(now)),
                command.SiteId,
                periodMonth,
                outstandingBytes,
                amount,
                price.Sequence,
                success.PaymentId,
                now),
            cancellationToken);

        return new DownloadOverageCheckoutDto(success.ConfirmationUrl, amount, outstandingBytes);
    }
}
