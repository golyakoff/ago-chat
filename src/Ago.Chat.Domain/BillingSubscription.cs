namespace Ago.Chat.Domain;

/// <summary>
/// `13-02`: the pending-intermediate-state row `10-02`/`12-02`'s own planning already anticipated as
/// necessary once a real payment provider entered the picture ("a checkout-session creation and a
/// webhook confirmation are two different moments, and `sites.tier`/`seat_limit` must not change until
/// the second one actually happens" - this item's own Scope). One row per checkout attempt, created
/// <see cref="Pending"/> the moment ЮKassa hands back a payment id, and moved to a terminal state only
/// by <see cref="BillingWebhookApplier"/>'s own transaction once a real webhook confirms the outcome -
/// never by the checkout-session-creation call itself, which only ever sees a redirect, not a payment
/// (`roadmap.md`'s own wording, restated in this item's Goal: "once ЮKassa's webhook confirms the
/// payment, never the redirect alone").
///
/// <para><b>Named <c>BillingSubscription</c>, not <c>PendingPayment</c>, even though every row this
/// item itself ever writes models one checkout attempt rather than a recurring cycle</b> - the backlog
/// item's own Scope names this exact table/type shape (<c>Stage13AddBillingSubscriptions</c>), and the
/// name is the right one for what the row becomes, not only what it starts as: `13-03`'s recurring
/// re-charge job is scoped to <i>extend this same row</i> (the stored <c>payment_method_id</c> a
/// successful <see cref="MarkSucceeded"/> records is exactly what a future renewal charges against),
/// not to create a new one per billing cycle. Calling it a "subscription" now, one item early, is what
/// keeps `13-03` an additive change to this aggregate instead of a rename forced on it later.</para>
///
/// <para>Its own aggregate, not folded into <see cref="Site"/> - the identical "does this change
/// independently, in its own transaction, with its own lifecycle" test <see cref="OperatorInvite"/>'s
/// own remarks apply to justify its separation from `Site`. Unlike an invite's redemption, this
/// aggregate's own terminal transition (<see cref="BillingWebhookApplier"/>'s job) *does* also write
/// `Site.Tier`/`Site.SeatLimit` in the same transaction - the same "one wider transaction than the
/// usual one-aggregate rule" shape `RegisterSiteHandler`'s bootstrap and
/// `OperatorInviteRedemptionRepository`'s redemption both already establish, for the identical reason:
/// a partial failure here must not leave a half-applied state (CLAUDE.md rule 4, `10-02`'s own
/// precedent).</para>
/// </summary>
public sealed class BillingSubscription
{
    public BillingSubscriptionId Id { get; }

    public SiteId SiteId { get; }

    /// <summary>ЮKassa's own payment id, assigned the moment
    /// <c>IYooKassaPaymentsClient.CreatePaymentAsync</c> returns - the natural key this item's own
    /// webhook applier looks a pending row up by, and half of the idempotency ledger's own composite
    /// key (<see cref="BillingWebhookEvent"/>'s own remarks).</summary>
    public string YooKassaPaymentId { get; } = string.Empty;

    public int RequestedSeats { get; }

    /// <summary><see cref="SubscriptionTierBands.TryResolveTier"/>'s own output, resolved once at
    /// checkout-session creation and carried on this row rather than re-derived from
    /// <see cref="RequestedSeats"/> at webhook time - <see cref="SubscriptionTierBands"/>'s own band
    /// table could in principle change between the two moments (a future re-pricing), and this row
    /// must apply the tier the customer was actually charged for, not whatever the table says
    /// today.</summary>
    public string Tier { get; } = string.Empty;

    public BillingSubscriptionStatus Status { get; private set; } = BillingSubscriptionStatus.Pending;

    /// <summary>ЮKassa's own reusable-charge handle, populated only by <see cref="MarkSucceeded"/> -
    /// `null` for every row that is still pending or ended up <see cref="BillingSubscriptionStatus.Failed"/>.
    /// `13-03`'s own recurring charge is this field's first real reader; this item only ever writes
    /// it.</summary>
    public string? PaymentMethodId { get; private set; }

    public DateTimeOffset CreatedAt { get; }

    private BillingSubscription(
        BillingSubscriptionId id,
        SiteId siteId,
        string yooKassaPaymentId,
        int requestedSeats,
        string tier,
        BillingSubscriptionStatus status,
        string? paymentMethodId,
        DateTimeOffset createdAt)
    {
        Id = id;
        SiteId = siteId;
        YooKassaPaymentId = yooKassaPaymentId;
        RequestedSeats = requestedSeats;
        Tier = tier;
        Status = status;
        PaymentMethodId = paymentMethodId;
        CreatedAt = createdAt;
    }

    // EF Core materialization only (1-04's precedent) - never called by domain code.
    private BillingSubscription()
    {
    }

    public static BillingSubscription Create(
        BillingSubscriptionId id, SiteId siteId, string yooKassaPaymentId, int requestedSeats, string tier, DateTimeOffset createdAt)
    {
        if (string.IsNullOrWhiteSpace(yooKassaPaymentId))
        {
            throw new ArgumentException("ЮKassa payment id cannot be empty.", nameof(yooKassaPaymentId));
        }

        return new BillingSubscription(
            id, siteId, yooKassaPaymentId, requestedSeats, tier, BillingSubscriptionStatus.Pending, paymentMethodId: null, createdAt);
    }

    /// <summary>Applied by <see cref="BillingWebhookApplier"/> on a verified, first-seen
    /// <c>payment.succeeded</c> event, in the same transaction as <see cref="Site.ActivateSubscription"/>.
    /// Throws on an already-terminal row rather than silently overwriting it - the idempotency ledger
    /// (<see cref="BillingWebhookEvent"/>) is what is supposed to stop a redelivered event from ever
    /// reaching this call a second time; reaching this guard at all means that ledger check was bypassed,
    /// the same "last line of defence for a caller that skipped the pre-check" shape
    /// <see cref="OperatorInvite.Redeem"/>'s own guard describes.</summary>
    public void MarkSucceeded(string? paymentMethodId)
    {
        if (Status != BillingSubscriptionStatus.Pending)
        {
            throw new InvalidOperationException(
                $"Billing subscription {Id.Value} is already {Status} and cannot be marked succeeded again.");
        }

        Status = BillingSubscriptionStatus.Succeeded;
        PaymentMethodId = paymentMethodId;
    }

    /// <summary>Applied on a verified, first-seen <c>payment.canceled</c> event -
    /// <see cref="Site.Tier"/>/<see cref="Site.SeatLimit"/> are never touched by this path (this item's
    /// own Scope: "they were never changed from free in the first place, since the pending row - not
    /// the site - held the in-flight state"), so this method's only job is recording the outcome on
    /// this row.</summary>
    public void MarkFailed()
    {
        if (Status != BillingSubscriptionStatus.Pending)
        {
            throw new InvalidOperationException(
                $"Billing subscription {Id.Value} is already {Status} and cannot be marked failed again.");
        }

        Status = BillingSubscriptionStatus.Failed;
    }
}
