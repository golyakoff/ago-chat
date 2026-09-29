using Ago.Chat.Application.Abstractions;
using Ago.Chat.Domain;
using Ago.Platform.Kernel;

namespace Ago.Chat.Application.UseCases.ChangeSubscriptionSeats;

/// <summary>
/// `13-03`/`decisions/0006`: an upgrade charges the prorated difference for the remainder of the
/// current period immediately, against the subscription's own stored <c>payment_method_id</c>, and
/// applies on the same verified-success discipline `13-02`'s checkout established (never before the
/// charge is confirmed - the port here returns a value only once ЮKassa has actually answered, unlike a
/// redirect). A downgrade makes no charge and no immediate write at all - it is only ever recorded,
/// applied later by the recurring-charge job.
///
/// <para><b>`26-299`: the proration formula itself now lives in <see cref="Domain.BillingProration"/>,
/// shared with every other mid-cycle charge in this codebase, not restated here.</b> This handler
/// resolves <c>periodStart</c> (<see cref="BillingSubscription.CurrentPeriodEnd"/> minus
/// <see cref="BillingSubscription.PeriodLength"/>) and hands the seat-band delta to
/// <see cref="Domain.BillingProration.Prorate"/> - see that type's own remarks for the floor rule and the
/// rounding-to-two-decimal-places-away-from-zero it applies (ЮKassa's own amount field is a fixed-point
/// decimal string with exactly two fraction digits, `YooKassaAmount`'s own `"F2"` formatting).
///
/// <para><b>`25-43`: <c>oldPrice</c> is read from the subscription's own stored price version, never
/// from the catalog's currently-effective one.</b> This is the correctness reason
/// <see cref="Domain.BillingSubscription.BaseSeatPriceVersion"/>/<see cref="Domain.BillingSubscription.ExtraSeatPriceVersion"/>
/// exist at all, found while wiring this handler to the new mechanism: if this recomputed "what the
/// tenant is already paying" from whatever <see cref="Abstractions.IPriceCatalogRepository.FindCurrentAsync"/>
/// answers <em>today</em>, a price change between the tenant's last charge and this upgrade would
/// silently reprice the period they already paid for - retroactively, through the proration math, not
/// through anything that looks like an edit to history. <c>oldPrice</c> reads
/// <see cref="Abstractions.IPriceCatalogRepository.FindVersionAsync"/> against the subscription's own
/// stored version numbers instead; only <c>newPrice</c> (what the upgraded seat count costs going
/// forward) reads the currently-effective one, the same "read fresh, at the moment of the decision"
/// discipline every other charge site uses for the price it is actually about to charge.</para>
/// </summary>
public sealed class ChangeSubscriptionSeatsHandler(
    IBillingSubscriptionRepository subscriptions,
    IPermissionChecker permissions,
    IYooKassaPaymentsClient yooKassa,
    IPriceCatalogRepository prices,
    ISeatChangeApplier applier,
    IIdGenerator idGenerator,
    IClock clock)
{
    public async Task<Result<ChangeSubscriptionSeatsResult>> HandleAsync(
        ChangeSubscriptionSeats command, CancellationToken cancellationToken)
    {
        var allowed = await permissions.HasPermissionAsync(
            command.RequestedBy, command.SiteId, Permission.SiteConfigure, cancellationToken);
        if (!allowed)
        {
            return ConversationErrors.Forbidden("Operator does not have permission to configure this site's billing.");
        }

        var subscription = await subscriptions.GetByIdAsync(command.SubscriptionId, command.SiteId, cancellationToken);
        if (subscription is null)
        {
            return ConversationErrors.BillingSubscriptionNotFound(command.SubscriptionId.Value);
        }

        // Only a currently-healthy subscription may change its seat count - a PastDue row has a more
        // pressing question (will the next retry even succeed) that a seat change would only complicate,
        // and this item's own Scope never asks for that interaction.
        if (subscription.Status != BillingSubscriptionStatus.Succeeded)
        {
            return ConversationErrors.BillingSubscriptionNotActive(
                $"Billing subscription {command.SubscriptionId.Value} is {subscription.Status}, not Succeeded, and cannot change its seat count.");
        }

        if (!SubscriptionTierBands.TryResolveTier(command.RequestedSeats, out var newTier))
        {
            return ConversationErrors.BillingInvalidSeatCount(
                $"{command.RequestedSeats} seats is not a purchasable seat count - expected "
                + $"{SubscriptionTierBands.MinSeats}-{SubscriptionTierBands.MaxSeats}.");
        }

        if (command.RequestedSeats == subscription.RequestedSeats)
        {
            return ConversationErrors.BillingSeatCountUnchanged();
        }

        return command.RequestedSeats > subscription.RequestedSeats
            ? await ApplyUpgradeAsync(command, subscription, newTier, cancellationToken)
            : await ScheduleDowngradeAsync(command, subscription, newTier, cancellationToken);
    }

    private async Task<Result<ChangeSubscriptionSeatsResult>> ApplyUpgradeAsync(
        ChangeSubscriptionSeats command, BillingSubscription subscription, string newTier, CancellationToken cancellationToken)
    {
        if (subscription.CurrentPeriodEnd is not { } periodEnd)
        {
            // Unreachable - a Succeeded row always has one (MarkSucceeded sets it unconditionally,
            // regardless of whether a payment method was ever saved).
            throw new InvalidOperationException(
                $"Billing subscription {command.SubscriptionId.Value} is Succeeded but has no period end.");
        }

        // `26-299`: reachable now that `savePaymentMethod` is the operator's own checkout-time choice -
        // see PurchaseAdministratorSlotHandler's own identical guard for the full reasoning.
        if (subscription.PaymentMethodId is not { Length: > 0 } paymentMethodId)
        {
            return ConversationErrors.BillingNoStoredPaymentMethod(command.SubscriptionId.Value);
        }

        // `25-43`: the price this subscription was actually last charged under - see this handler's
        // own remarks for why this must be a historical lookup, never the catalog's current answer.
        var oldBasePrice = await prices.FindVersionAsync(SubscriptionTierBands.BaseSeatPriceKey, subscription.BaseSeatPriceVersion, cancellationToken);
        var oldExtraPrice = await prices.FindVersionAsync(SubscriptionTierBands.ExtraSeatPriceKey, subscription.ExtraSeatPriceVersion, cancellationToken);
        if (oldBasePrice is null || oldExtraPrice is null)
        {
            // Unreachable in a correctly configured deployment - a Succeeded subscription's own stored
            // version numbers name versions this same mechanism minted and never deletes
            // (Domain.PublishedPriceVersion's own "insert-only, never removed" remarks). Thrown, not
            // translated, the same posture the PaymentMethodId/CurrentPeriodEnd guard just above uses.
            throw new InvalidOperationException(
                $"Billing subscription {command.SubscriptionId.Value}'s own stored price version "
                + $"({subscription.BaseSeatPriceVersion}/{subscription.ExtraSeatPriceVersion}) no longer exists - a published "
                + "price version must never be deleted.");
        }

        // The currently-effective price - what the upgraded seat count costs going forward, read
        // fresh, never cached, the identical discipline every other real charge site uses.
        var newBasePrice = await prices.FindCurrentAsync(SubscriptionTierBands.BaseSeatPriceKey, cancellationToken);
        if (newBasePrice is null)
        {
            return PriceCatalogErrors.PriceNotConfigured(SubscriptionTierBands.BaseSeatPriceKey.Value);
        }

        var newExtraPrice = await prices.FindCurrentAsync(SubscriptionTierBands.ExtraSeatPriceKey, cancellationToken);
        if (newExtraPrice is null)
        {
            return PriceCatalogErrors.PriceNotConfigured(SubscriptionTierBands.ExtraSeatPriceKey.Value);
        }

        var now = clock.UtcNow;
        var periodStart = periodEnd - BillingSubscription.PeriodLength;

        var oldPrice = SubscriptionTierBands.ComputeSeatPriceRub(subscription.RequestedSeats, oldBasePrice.AmountRub, oldExtraPrice.AmountRub);
        var newPrice = SubscriptionTierBands.ComputeSeatPriceRub(command.RequestedSeats, newBasePrice.AmountRub, newExtraPrice.AmountRub);
        // `26-299`: BillingProration.Prorate, not a hand-rolled remaining-days fraction - see that type's
        // own remarks for the floor rule this now applies (a day-one upgrade charges the full delta,
        // never a few cents short from the seconds already elapsed that day).
        var proratedAmount = BillingProration.Prorate(newPrice - oldPrice, now, periodStart, BillingSubscription.PeriodLength);

        var idempotenceKey = idGenerator.NewId(now).ToString();
        var description = $"AGO Chat - upgrade to {newTier} tier, {command.RequestedSeats} seats (prorated)";
        var chargeResult = await yooKassa.ChargeStoredPaymentMethodAsync(
            new ChargeStoredPaymentMethodRequest(proratedAmount, description, paymentMethodId, idempotenceKey), cancellationToken);

        if (chargeResult is ChargeStoredPaymentMethodResult.Refused refused)
        {
            return ConversationErrors.BillingPaymentProviderRefused(refused.Reason);
        }

        await applier.ApplyImmediateIncreaseAsync(
            new SeatChangeApplyRequest(
                command.SubscriptionId, command.SiteId, command.RequestedSeats, newTier, newBasePrice.Sequence, newExtraPrice.Sequence, now),
            cancellationToken);

        return new ChangeSubscriptionSeatsResult.Upgraded(proratedAmount, newTier, command.RequestedSeats);
    }

    private async Task<Result<ChangeSubscriptionSeatsResult>> ScheduleDowngradeAsync(
        ChangeSubscriptionSeats command, BillingSubscription subscription, string newTier, CancellationToken cancellationToken)
    {
        subscription.ScheduleSeatDecrease(command.RequestedSeats, newTier);
        await subscriptions.UpdateAsync(subscription, cancellationToken);

        return new ChangeSubscriptionSeatsResult.DowngradeScheduled(newTier, command.RequestedSeats);
    }
}
