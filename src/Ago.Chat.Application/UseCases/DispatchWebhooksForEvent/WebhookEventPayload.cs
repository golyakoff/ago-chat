namespace Ago.Chat.Application.UseCases.DispatchWebhooksForEvent;

/// <summary>
/// The tenant-facing wire shape of one webhook delivery - `event_type`, the conversation id, and
/// timestamps, deliberately nothing else (backlog's own scope note: "never a message body, matching
/// this project's own 'never log/transmit message content beyond what's necessary' instinct
/// elsewhere"). Serialized with `Ago.Chat.Contracts.WireJsonOptions.Options` (camelCase) - the same
/// options every fan-out handler already uses to keep this codebase's outward-facing JSON one
/// consistent shape, reused rather than a second, identically-configured `JsonSerializerOptions`
/// instance living here too.
///
/// A plain record with no behaviour, deliberately in Application rather than
/// `Ago.Chat.Contracts` - it is not an integration event published to the broker (`messaging.md`'s
/// "Contracts... a public API [to consumers of the broker]" does not describe this: this is the HTTP
/// body sent to a *tenant's* endpoint, a completely different wire boundary with its own versioning
/// story that `6-05`'s own scope never asked for).
/// </summary>
public sealed record WebhookEventPayload(string EventType, Guid ConversationId, DateTimeOffset OccurredAt);

/// <summary>
/// The two tenant-facing event-type strings this dispatcher ever sends - dot-case, Stripe/GitHub-style
/// (matching `adr/0024`'s own framing for the signature scheme), distinct from the internal RabbitMQ
/// topic names (`nameof(ConversationAssignedToOperator)`/`nameof(ConversationEnded)`) on purpose: a
/// tenant's receiver should never have to know this system's internal contract-type names, only a
/// small, stable, publicly documented vocabulary.
/// </summary>
public static class WebhookEventTypes
{
    public const string ConversationAssigned = "conversation.assigned";

    public const string ConversationClosed = "conversation.closed";
}
