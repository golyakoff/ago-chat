using Ago.Chat.Application.Abstractions;
using Ago.Chat.Domain;
using Ago.Platform.Kernel;

namespace Ago.Chat.Application.UseCases.CreateCheckoutSession;

/// <summary>
/// `13-02`: the first-payment path's own entry point - validates the requested seat count against
/// <see cref="SubscriptionTierBands"/>, computes the flat per-seat charge, calls out to ЮKassa
/// (`IYooKassaPaymentsClient`, a provider-neutral Application port - `adr/0025`), and records a
/// <see cref="BillingSubscription"/> row in <see cref="BillingSubscriptionStatus.Pending"/> before
/// returning the confirmation URL the caller redirects the operator's browser to. Never touches
/// `Site.Tier`/`Site.SeatLimit` itself - only a verified webhook does that
/// (`ProcessYooKassaWebhookHandler`), per this item's own Goal ("never the redirect alone").
///
/// <para>Deliberately no retry/circuit-breaker wrapping around <see cref="IYooKassaPaymentsClient"/> -
/// unlike `14-01`'s inbound channel adapters (wrapped by `ResilientInboundChannelAdapter` because a
/// background consumer must keep running through a provider outage), this is one ordinary synchronous
/// call inside one operator-initiated HTTP request; a transient failure surfaces as an ordinary `5xx`
/// the operator's own client can retry by clicking again, the same "no resilience machinery for a
/// low-frequency, human-driven write" judgement `UpdateWidgetConfigHandler`'s own remarks make for a
/// different reason (no shared multi-conversation transaction to protect, no hot path to shield).</para>
/// </summary>
public sealed class CreateCheckoutSessionHandler(
    ISiteRepository sites,
    IPermissionChecker permissions,
    IBillingSubscriptionRepository subscriptions,
    IYooKassaPaymentsClient yooKassa,
    BillingOptions billingOptions,
    IIdGenerator idGenerator,
    IClock clock)
{
    public async Task<Result<CheckoutSessionDto>> HandleAsync(CreateCheckoutSession command, CancellationToken cancellationToken)
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

        if (!SubscriptionTierBands.TryResolveTier(command.RequestedSeats, out var tier))
        {
            return ConversationErrors.BillingInvalidSeatCount(
                $"{command.RequestedSeats} seats is not a purchasable seat count - expected "
                + $"{SubscriptionTierBands.MinSeats}-{SubscriptionTierBands.MaxSeats}.");
        }

        var now = clock.UtcNow;
        var amount = billingOptions.PricePerSeatRub * command.RequestedSeats;
        var idempotenceKey = idGenerator.NewId(now).ToString();

        var paymentResult = await yooKassa.CreatePaymentAsync(
            new CreatePaymentRequest(
                amount,
                $"AGO Chat - {tier} tier, {command.RequestedSeats} seats",
                billingOptions.CheckoutReturnUrl,
                idempotenceKey),
            cancellationToken);

        if (paymentResult is CreatePaymentResult.Refused refused)
        {
            return ConversationErrors.BillingPaymentProviderRefused(refused.Reason);
        }

        var success = (CreatePaymentResult.Success)paymentResult;
        var subscriptionId = new BillingSubscriptionId(idGenerator.NewId(now));
        var subscription = BillingSubscription.Create(
            subscriptionId, command.SiteId, success.PaymentId, command.RequestedSeats, tier, now);
        await subscriptions.SaveAsync(subscription, cancellationToken);

        return new CheckoutSessionDto(success.ConfirmationUrl);
    }
}
