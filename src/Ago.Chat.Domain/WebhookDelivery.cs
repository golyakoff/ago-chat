namespace Ago.Chat.Domain;

/// <summary>
/// `6-03`: one attempt-record row a future dispatcher (`6-05`) will write and this item's own tests
/// write directly at the repository level (this item's own scope - "actually sending... is `6-05`'s
/// job, this item's tests write `webhook_deliveries` rows directly"). <see cref="Record"/> is
/// therefore a plain, fully-parameterised factory rather than a "start pending, transition later"
/// state machine with methods like `Attachment.ConfirmReady` - there is no real caller to transition
/// it yet, and adding transition methods ahead of `6-05`'s actual dispatch logic would be exactly the
/// "no domain-event plumbing ahead of a real subscriber" anti-pattern this codebase's own precedent
/// (`Attachment`'s remarks) warns against.
///
/// `6-05`: <see cref="MessageId"/> added - the source event's stable `EventEnvelope.MessageId`
/// (messaging.md). One row per <c>(EndpointId, MessageId)</c> pair, holding the *final* outcome of
/// however many HTTP attempts the dispatcher made (<see cref="Attempt"/> is the count, not a row
/// index) - this is the "shape for recording a retried attempt" this class's own remarks above left
/// for `6-05` to decide, and one-summary-row-per-delivery (not one row per individual attempt) is the
/// decision: it is what a tenant's delivery-history view actually wants to see
/// (`GetWebhookDeliveriesHandler`), and a unique index on <c>(endpoint_id, message_id)</c>
/// (`WebhookDeliveryConfiguration`) is what turns this same table into the idempotency ledger
/// `resilience.md`/the backlog ask for - "keyed by (message_id, endpoint_id)" is this constraint,
/// literally, not a second parallel table.
/// </summary>
public sealed class WebhookDelivery
{
    /// <summary>`response_snippet` is explicitly bounded (this item's own scope: "never store an
    /// unbounded response body") - enforced here, not only by the database column's own length limit,
    /// so the invariant holds regardless of which caller constructs a delivery.</summary>
    public const int MaxResponseSnippetLength = 2000;

    public WebhookDeliveryId Id { get; }

    public WebhookEndpointId EndpointId { get; }

    /// <summary>The source event's <c>EventEnvelope.MessageId</c> - stable across a broker redelivery
    /// of the same event, which is exactly what makes it the right idempotency key
    /// (messaging.md: "every consumer records message_id"). Paired with <see cref="EndpointId"/> via
    /// this table's own unique index, not the platform's generic `inbox` table
    /// (`Ago.Platform.Abstractions.IInboxChecker`) - that table is keyed by
    /// <c>(message_id, consumer)</c>, one row per logical *consumer type*, not per endpoint a single
    /// event fans out to; this dispatcher needs the finer <c>(message_id, endpoint_id)</c> grain the
    /// backlog names explicitly, which this table's own natural key already gives it for free.</summary>
    public Guid MessageId { get; }

    public string EventType { get; } = string.Empty;

    /// <summary>Raw JSON text - `jsonb` at rest (`WebhookDeliveryConfiguration`, Infrastructure); a
    /// plain <see cref="string"/> here for the same reason <see cref="Attachment.ObjectKey"/> is a
    /// plain string rather than a platform type: Domain's only allowed dependency is
    /// `Ago.Platform.Kernel`, one package short of anything that would model a JSON document.</summary>
    public string Payload { get; } = string.Empty;

    public int Attempt { get; }

    public WebhookDeliveryStatus Status { get; }

    public int? ResponseStatus { get; }

    public string? ResponseSnippet { get; }

    public DateTimeOffset CreatedAt { get; }

    public DateTimeOffset? DeliveredAt { get; }

    private WebhookDelivery(
        WebhookDeliveryId id,
        WebhookEndpointId endpointId,
        Guid messageId,
        string eventType,
        string payload,
        int attempt,
        WebhookDeliveryStatus status,
        int? responseStatus,
        string? responseSnippet,
        DateTimeOffset createdAt,
        DateTimeOffset? deliveredAt)
    {
        Id = id;
        EndpointId = endpointId;
        MessageId = messageId;
        EventType = eventType;
        Payload = payload;
        Attempt = attempt;
        Status = status;
        ResponseStatus = responseStatus;
        ResponseSnippet = Truncate(responseSnippet);
        CreatedAt = createdAt;
        DeliveredAt = deliveredAt;
    }

    // EF Core materialization only (1-04's precedent) - never called by domain code.
    private WebhookDelivery()
    {
    }

    public static WebhookDelivery Record(
        WebhookDeliveryId id,
        WebhookEndpointId endpointId,
        Guid messageId,
        string eventType,
        string payload,
        int attempt,
        WebhookDeliveryStatus status,
        int? responseStatus,
        string? responseSnippet,
        DateTimeOffset createdAt,
        DateTimeOffset? deliveredAt) =>
        new(id, endpointId, messageId, eventType, payload, attempt, status, responseStatus, responseSnippet, createdAt, deliveredAt);

    private static string? Truncate(string? responseSnippet) =>
        responseSnippet is { Length: > MaxResponseSnippetLength }
            ? responseSnippet[..MaxResponseSnippetLength]
            : responseSnippet;
}
