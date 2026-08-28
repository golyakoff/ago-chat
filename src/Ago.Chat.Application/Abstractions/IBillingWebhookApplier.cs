using Ago.Chat.Domain;

namespace Ago.Chat.Application.Abstractions;

/// <summary>
/// `13-02`: the one database transaction this item's backlog describes - idempotency ledger insert,
/// then (on a new `payment.succeeded`) the pending row's own terminal transition plus `Site.Tier`/
/// `Site.SeatLimit`, committed together or not at all. Its own port, not folded into
/// <see cref="IBillingSubscriptionRepository"/> or <see cref="ISiteRepository"/> - neither of those
/// single-aggregate ports has any business writing rows that belong to a different aggregate, the same
/// "its own port because it writes across more than one aggregate" reasoning
/// <see cref="ISiteRegistrationRepository"/>/<see cref="IOperatorInviteRedemptionRepository"/> already
/// establish for the analogous multi-aggregate writes elsewhere in this codebase.
/// </summary>
public interface IBillingWebhookApplier
{
    Task<BillingWebhookApplyResult> ApplyAsync(BillingWebhookApplyRequest request, CancellationToken cancellationToken);
}

public sealed record BillingWebhookApplyRequest(string YooKassaPaymentId, string EventType, string? PaymentMethodId, DateTimeOffset Now);

/// <summary>Every outcome <see cref="ProcessYooKassaWebhookHandler"/> and this item's own Done-when
/// name, as a closed hierarchy rather than an enum+nullable-payload pair - the same
/// <c>OperatorInviteRedemptionResult</c> shape and the same reason: the compiler forces every call site
/// to handle every case.</summary>
public abstract record BillingWebhookApplyResult
{
    private BillingWebhookApplyResult()
    {
    }

    /// <summary>The `(yookassa_payment_id, event_type)` pair was already recorded - this item's own
    /// idempotency ledger doing its one job. No further write happened; the caller still acks `200`
    /// (backlog: "skipped, and still acked 200").</summary>
    public sealed record Duplicate : BillingWebhookApplyResult;

    /// <summary>No <c>billing_subscriptions</c> row matches this payment id - a webhook for a payment
    /// this deployment never created a checkout session for (a stale test notification, a payment id
    /// typo, or a genuine ЮKassa-side anomaly). The ledger row is still committed (so a real redelivery
    /// of the identical event is still caught as <see cref="Duplicate"/> next time), but there is no
    /// site to act on - acked `200` regardless, since retrying will never make a nonexistent row
    /// appear.</summary>
    public sealed record SubscriptionNotFound : BillingWebhookApplyResult;

    /// <summary>A new `payment.succeeded` event, applied: the pending row is now `Succeeded` and
    /// `Site.Tier`/`Site.SeatLimit` now read <paramref name="Tier"/>/<paramref name="SeatLimit"/>.</summary>
    public sealed record Applied(SiteId SiteId, string Tier, int SeatLimit) : BillingWebhookApplyResult;

    /// <summary>A new `payment.canceled` event, applied: the pending row is now `Failed`.
    /// `Site.Tier`/`Site.SeatLimit` are untouched - this item's own Scope: "they were never changed from
    /// free in the first place".</summary>
    public sealed record Canceled : BillingWebhookApplyResult;

    /// <summary>A new, first-seen event of a type this item has no handling for (e.g.
    /// `payment.waiting_for_capture`) - recorded in the ledger (so a real redelivery is still caught),
    /// otherwise a no-op. Acked `200`: an event this deployment does not act on is not a failure ЮKassa
    /// should retry.</summary>
    public sealed record Ignored : BillingWebhookApplyResult;
}
