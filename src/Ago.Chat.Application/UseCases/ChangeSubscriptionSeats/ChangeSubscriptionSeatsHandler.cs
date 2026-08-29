using Ago.Chat.Application.Abstractions;
using Ago.Chat.Application.UseCases.CreateCheckoutSession;
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
/// <para><b>The proration formula, stated because the backlog left the rounding rule as this item's own
/// call.</b> <c>(new_price - old_price) * remaining_days / period_length_days</c>, both prices computed
/// from the flat <see cref="BillingOptions.PricePerSeatRub"/> (`SubscriptionTierBands`' own bands carry
/// no separate per-tier price - seats are the only variable), <c>remaining_days</c> clamped to
/// <c>[0, PeriodLength]</c> against the subscription's own real <see cref="BillingSubscription.CurrentPeriodEnd"/>,
/// and the result rounded to two decimal places, away from zero - ЮKassa's own amount field is a
/// fixed-point decimal string with exactly two fraction digits (<c>YooKassaAmount</c>'s own
/// `"F2"` formatting), so a rounding rule has to exist somewhere, and "round the customer's own favour
/// on a tie" is the same direction `CreateCheckoutSessionHandler`'s plain multiplication already rounds
/// implicitly (a whole number of seats times a whole-Rouble price never needs rounding at all, so this
/// is the first call site that actually exercises the choice).</para>
/// </summary>
public sealed class ChangeSubscriptionSeatsHandler(
    IBillingSubscriptionRepository subscriptions,
    IPermissionChecker permissions,
    IYooKassaPaymentsClient yooKassa,
    ISeatChangeApplier applier,
    BillingOptions billingOptions,
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
        if (subscription.PaymentMethodId is not { Length: > 0 } paymentMethodId || subscription.CurrentPeriodEnd is not { } periodEnd)
        {
            // Unreachable - a Succeeded row always has both (MarkSucceeded sets them together).
            throw new InvalidOperationException(
                $"Billing subscription {command.SubscriptionId.Value} is Succeeded but has no payment method or period end.");
        }

        var now = clock.UtcNow;
        var periodLengthDays = (decimal)BillingSubscription.PeriodLength.TotalDays;
        var remainingDays = Math.Clamp((decimal)(periodEnd - now).TotalDays, 0m, periodLengthDays);

        var oldPrice = billingOptions.PricePerSeatRub * subscription.RequestedSeats;
        var newPrice = billingOptions.PricePerSeatRub * command.RequestedSeats;
        var proratedAmount = Math.Round((newPrice - oldPrice) * remainingDays / periodLengthDays, 2, MidpointRounding.AwayFromZero);

        var idempotenceKey = idGenerator.NewId(now).ToString();
        var description = $"AGO Chat - upgrade to {newTier} tier, {command.RequestedSeats} seats (prorated)";
        var chargeResult = await yooKassa.ChargeStoredPaymentMethodAsync(
            new ChargeStoredPaymentMethodRequest(proratedAmount, description, paymentMethodId, idempotenceKey), cancellationToken);

        if (chargeResult is ChargeStoredPaymentMethodResult.Refused refused)
        {
            return ConversationErrors.BillingPaymentProviderRefused(refused.Reason);
        }

        await applier.ApplyImmediateIncreaseAsync(
            new SeatChangeApplyRequest(command.SubscriptionId, command.SiteId, command.RequestedSeats, newTier, now), cancellationToken);

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
