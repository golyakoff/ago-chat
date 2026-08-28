namespace Ago.Chat.Domain;

/// <summary>
/// `13-02`: the inbound idempotency ledger this item's own backlog note asks for, adapted from `6-05`'s
/// outbound <c>WebhookDelivery</c> shape (`(endpoint_id, message_id)` unique index doubling as both
/// history and ledger) to this item's different situation - there is exactly one "sender" here (ЮKassa
/// itself), so the natural composite key is <see cref="YooKassaPaymentId"/> + <see cref="EventType"/>
/// (which *event* fired for a given payment: <c>payment.succeeded</c>, <c>payment.waiting_for_capture</c>
/// and <c>payment.canceled</c> can all arrive for the same payment id), not which endpoint received it -
/// there is only ever one receiver.
///
/// <para><b>Its own table, not folded into <see cref="BillingSubscription"/>.</b> A subscription row is
/// 1:1 with a checkout attempt; this ledger is 1:many with one (a payment can legitimately receive more
/// than one distinct event type, and - the case this table exists to catch - ЮKassa's own at-least-once
/// delivery can redeliver the identical event). Folding the two would mean either a subscription row
/// with a variable number of "which events have I seen" columns, or losing the redelivery history
/// `WebhookDelivery`'s own precedent keeps as an audit trail - this table gets both for the cost of one
/// more `CREATE TABLE`.</para>
///
/// <para><see cref="BillingWebhookApplier"/> is this ledger's only writer: inserts a row inside its own
/// transaction before touching anything else, and treats the resulting unique-constraint violation as
/// "already recorded, no-op" - `messaging.md`'s "handlers must be safe to run twice regardless of the
/// inbox" discipline, realized as a plain transactional insert-and-catch rather than a broker consumer,
/// because this is an HTTP-triggered write, not a message-broker one (see `adr/0025`'s own remarks on
/// why the platform's generic `inbox` table, keyed by `(message_id, consumer)`, is the wrong shape
/// here too - there is no `EventEnvelope.MessageId` on an inbound third-party webhook, and no consumer
/// in the broker sense).</para>
/// </summary>
public sealed class BillingWebhookEvent
{
    public BillingWebhookEventId Id { get; }

    public string YooKassaPaymentId { get; } = string.Empty;

    /// <summary>ЮKassa's own event name - <c>payment.succeeded</c>, <c>payment.waiting_for_capture</c>,
    /// <c>payment.canceled</c>, or any other value ЮKassa's own webhook contract may send. A plain
    /// string, not an enum: this ledger's job is to record whatever ЮKassa actually sent, including an
    /// event type this item has no handling for yet - narrowing it to a closed set here would make an
    /// unrecognised-but-real event fail to even record, rather than being recorded and simply
    /// ignored.</summary>
    public string EventType { get; } = string.Empty;

    public DateTimeOffset ReceivedAt { get; }

    private BillingWebhookEvent(BillingWebhookEventId id, string yooKassaPaymentId, string eventType, DateTimeOffset receivedAt)
    {
        Id = id;
        YooKassaPaymentId = yooKassaPaymentId;
        EventType = eventType;
        ReceivedAt = receivedAt;
    }

    // EF Core materialization only (1-04's precedent) - never called by domain code.
    private BillingWebhookEvent()
    {
    }

    public static BillingWebhookEvent Record(BillingWebhookEventId id, string yooKassaPaymentId, string eventType, DateTimeOffset receivedAt) =>
        new(id, yooKassaPaymentId, eventType, receivedAt);
}
