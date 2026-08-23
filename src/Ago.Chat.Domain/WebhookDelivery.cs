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
/// </summary>
public sealed class WebhookDelivery
{
    /// <summary>`response_snippet` is explicitly bounded (this item's own scope: "never store an
    /// unbounded response body") - enforced here, not only by the database column's own length limit,
    /// so the invariant holds regardless of which caller constructs a delivery.</summary>
    public const int MaxResponseSnippetLength = 2000;

    public WebhookDeliveryId Id { get; }

    public WebhookEndpointId EndpointId { get; }

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
        string eventType,
        string payload,
        int attempt,
        WebhookDeliveryStatus status,
        int? responseStatus,
        string? responseSnippet,
        DateTimeOffset createdAt,
        DateTimeOffset? deliveredAt) =>
        new(id, endpointId, eventType, payload, attempt, status, responseStatus, responseSnippet, createdAt, deliveredAt);

    private static string? Truncate(string? responseSnippet) =>
        responseSnippet is { Length: > MaxResponseSnippetLength }
            ? responseSnippet[..MaxResponseSnippetLength]
            : responseSnippet;
}
