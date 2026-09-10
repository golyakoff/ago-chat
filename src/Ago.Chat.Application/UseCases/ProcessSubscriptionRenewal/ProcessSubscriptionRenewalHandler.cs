using Ago.Chat.Application.Abstractions;
using Ago.Chat.Application.UseCases.CreateCheckoutSession;
using Ago.Chat.Domain;
using Ago.Platform.Kernel;

namespace Ago.Chat.Application.UseCases.ProcessSubscriptionRenewal;

/// <summary>
/// `13-03`: one due <see cref="BillingSubscription"/> row, end to end - decide which branch applies from
/// a plain (unlocked) read, make at most one outbound ЮKassa call if the branch needs one, then commit
/// the verified outcome through <see cref="ISubscriptionRenewalApplier"/>. `Ago.Chat.Worker`'s own
/// recurring-charge job calls this once per candidate, in its own <see cref="IServiceScopeFactory"/>
/// scope - the same "singleton job, scoped Application handler" split `AutoCloseInactiveConversationsJob`
/// already establishes.
///
/// <para><b>Branch order, and why it is exactly this order.</b>
/// <list type="number">
/// <item><see cref="BillingSubscription.CancelRequested"/> first, regardless of status - `decisions/0006`'s
/// "no charge attempt, successful or otherwise" once cancelled, so this must be checked before any
/// charge decision, not after.</item>
/// <item><see cref="BillingSubscription.HasExhaustedRetryWindow"/> second - a `PastDue` row whose window
/// has just closed on this exact tick must lapse, not take one more retry, even though
/// <see cref="BillingSubscription.IsRetryDue"/> may also be `true` on the same tick (the day-7 retry and
/// the day-7 expiry are the same tick by construction).</item>
/// <item>Otherwise, a charge - <see cref="BillingSubscription.IsDueForRenewal"/> (an on-time renewal) or
/// <see cref="BillingSubscription.IsRetryDue"/> (a retry inside the window).</item>
/// </list>
/// </para>
///
/// <para><b>`25-43`: the identical currently-effective-price read `CreateCheckoutSessionHandler` makes
/// at checkout, made again here at renewal.</b> This is `25-43`'s own "a tenant mid-renewal when the
/// price changes is not a special case" point, made concrete: this handler does not know or care
/// whether the price changed since the last renewal - it reads whatever
/// <see cref="Abstractions.IPriceCatalogRepository"/> says is effective right now, the ordinary
/// "read fresh, at the moment of the decision" discipline that already produces the correct, honest
/// answer with no grace period or reconciliation rule to invent. A missing price refuses the renewal
/// the identical way it refuses a first checkout - <see cref="PriceCatalogErrors.PriceNotConfigured"/>,
/// thrown here rather than returned as a `Result`, matching the "unreachable in a correctly configured
/// deployment" posture this handler's own `PaymentMethodId` guard already uses just above: a
/// subscription that reached `Succeeded` at least once already proved both keys were priced at that
/// moment, so either becoming unpublished before the next renewal is a deployment regression, not an
/// ordinary outcome this handler's own caller (the Worker job) has anything more specific to do about
/// than log and retry next tick.</para>
///
/// <para>No resilience wrapping written here - `Ago.Chat.Module`'s own composition root decorates
/// <see cref="IYooKassaPaymentsClient"/> for this specific call (`ResilientYooKassaPaymentsClient`), the
/// same "Application calls the port, the decorator supplies the pipeline" shape
/// `ResilientInboundChannelAdapter` already establishes - this handler stays unaware resilience exists at
/// all, and a transient fault simply propagates as a thrown exception the job's own per-candidate
/// `try`/`catch` logs and retries next tick, exactly like every other Worker sweep in this
/// codebase.</para>
/// </summary>
public sealed class ProcessSubscriptionRenewalHandler(
    IBillingSubscriptionRepository subscriptions,
    IYooKassaPaymentsClient yooKassa,
    IPriceCatalogRepository prices,
    ISubscriptionRenewalApplier applier,
    IClock clock)
{
    public async Task<SubscriptionRenewalOutcome> HandleAsync(ProcessSubscriptionRenewal command, CancellationToken cancellationToken)
    {
        var now = clock.UtcNow;
        var subscription = await subscriptions.GetByIdAsync(command.SubscriptionId, cancellationToken);
        if (subscription is null)
        {
            return new SubscriptionRenewalOutcome.NotDue();
        }

        if (subscription.CancelRequested && (subscription.IsDueForRenewal(now) || subscription.IsRetryDue(now) || subscription.Status == BillingSubscriptionStatus.PastDue))
        {
            await applier.ApplyLapseAsync(command.SubscriptionId, now, cancellationToken);
            return new SubscriptionRenewalOutcome.Lapsed();
        }

        if (subscription.HasExhaustedRetryWindow(now))
        {
            await applier.ApplyLapseAsync(command.SubscriptionId, now, cancellationToken);
            return new SubscriptionRenewalOutcome.Lapsed();
        }

        if (!subscription.IsDueForRenewal(now) && !subscription.IsRetryDue(now))
        {
            // Selected as a candidate, no longer due - a redundant tick (a shorter-than-a-day poll
            // interval revisiting a row it already handled today) or a second replica that already
            // processed it. See ChargeStoredPaymentMethodRequest's own remarks for why a genuine
            // double-dispatch of the charge itself is still safe.
            return new SubscriptionRenewalOutcome.NotDue();
        }

        if (subscription.PaymentMethodId is not { Length: > 0 } paymentMethodId)
        {
            // Unreachable in practice - MarkSucceeded always sets this before CurrentPeriodEnd exists
            // for the first time, so a due-for-renewal or PastDue row always has one. Thrown, not
            // translated into an outcome case, the same "unreachable, thrown rather than translated"
            // shape BillingWebhookApplier's own missing-site guard describes.
            throw new InvalidOperationException(
                $"Billing subscription {command.SubscriptionId.Value} is due for renewal but has no stored payment method id.");
        }

        if (subscription.IsOption)
        {
            // `23-86` deliberately does not invent a price for an option's own recurring charge - "no
            // price, anywhere" (this item's own Scope); the seat-pricing formula below is meaningless
            // for an option row (RequestedSeats is always zero there - BillingSubscription.OptionKey's
            // own remarks). Charging Rub 0 or reusing the seat formula would both be silently wrong, so
            // this refuses loudly instead - the same "unreachable, thrown rather than translated" shape
            // the PaymentMethodId guard just above already uses. No production code path creates a
            // due-for-renewal option row today (that is `23-115`'s own scope, "a tenant can buy an
            // option themselves") - reaching this is itself the signal that a price source for an
            // option's recurring charge still needs to be supplied before that item ships.
            throw new InvalidOperationException(
                $"Billing subscription {command.SubscriptionId.Value} is an option subscription (key "
                + $"'{subscription.OptionKey!.Value.Value}') due for renewal, but no price source for an option's "
                + "recurring charge exists yet - see this item's own report.");
        }

        var basePrice = await prices.FindCurrentAsync(SubscriptionTierBands.BaseSeatPriceKey, cancellationToken)
            ?? throw new InvalidOperationException(
                $"Billing subscription {command.SubscriptionId.Value} is due for renewal but "
                + $"'{SubscriptionTierBands.BaseSeatPriceKey.Value}' has no published price - see this handler's own remarks.");
        var extraPrice = await prices.FindCurrentAsync(SubscriptionTierBands.ExtraSeatPriceKey, cancellationToken)
            ?? throw new InvalidOperationException(
                $"Billing subscription {command.SubscriptionId.Value} is due for renewal but "
                + $"'{SubscriptionTierBands.ExtraSeatPriceKey.Value}' has no published price - see this handler's own remarks.");

        var amount = SubscriptionTierBands.ComputeSeatPriceRub(subscription.RequestedSeats, basePrice.AmountRub, extraPrice.AmountRub);
        var description = $"AGO Chat - {subscription.Tier} tier renewal, {subscription.RequestedSeats} seats";
        // Deterministic, not a fresh id per call - ChargeStoredPaymentMethodRequest's own remarks on why
        // this is what makes a two-replica race over the same due row safe rather than a double charge.
        var idempotenceKey = $"renewal:{command.SubscriptionId.Value}:{now:yyyy-MM-dd}";

        var chargeResult = await yooKassa.ChargeStoredPaymentMethodAsync(
            new ChargeStoredPaymentMethodRequest(amount, description, paymentMethodId, idempotenceKey), cancellationToken);

        switch (chargeResult)
        {
            case ChargeStoredPaymentMethodResult.Success:
                await applier.ApplyRenewalSuccessAsync(
                    command.SubscriptionId, now, basePrice.Sequence, extraPrice.Sequence, cancellationToken);
                return new SubscriptionRenewalOutcome.Renewed();

            case ChargeStoredPaymentMethodResult.Refused refused:
                await applier.ApplyRenewalFailureAsync(command.SubscriptionId, now, cancellationToken);
                return new SubscriptionRenewalOutcome.ChargeRefused(refused.Reason);

            default:
                throw new InvalidOperationException($"Unhandled {nameof(ChargeStoredPaymentMethodResult)} case: {chargeResult.GetType().Name}.");
        }
    }
}
