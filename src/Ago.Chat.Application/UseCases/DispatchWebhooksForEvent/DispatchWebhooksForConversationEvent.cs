using Ago.Chat.Domain;

namespace Ago.Chat.Application.UseCases.DispatchWebhooksForEvent;

/// <summary>
/// `6-05`: one command per source event (`ConversationAssignedToOperator` or `ConversationEnded`),
/// shared by both consumers - the fan-out-to-every-endpoint logic is identical regardless of which
/// topic triggered it, only <see cref="EventType"/> and the site/conversation identifiers differ.
/// <see cref="MessageId"/> is the source `EventEnvelope.MessageId`, not a freshly generated one - it
/// is what the idempotency ledger (`WebhookDeliveryConfiguration`'s unique index) keys on, so a
/// redelivery of the *same* envelope must arrive here carrying the *same* value
/// (`ConversationAssignmentWebhookDispatchConsumer`/`ConversationClosedWebhookDispatchConsumer` both
/// read it straight off the envelope they were handed, never regenerate it).
///
/// Named <c>DispatchWebhooksForConversationEvent</c>, not the bare <c>DispatchWebhooksForEvent</c>
/// this use case's own folder is named - the same "no type alias for a namespace collision, rename the
/// type instead" rule this project already applies elsewhere (`RecordUnreadMessage` living in a
/// `RecordUnread` folder is the precedent): a type sharing its exact name with its own containing
/// namespace segment forces every consumer, including this file's own test project mirroring the same
/// folder name, into an awkward fully-qualified reference or a `using X = Y` alias CLAUDE.md rules out
/// outright.
/// </summary>
public sealed record DispatchWebhooksForConversationEvent(
    Guid MessageId, SiteId SiteId, Guid ConversationId, string EventType, DateTimeOffset OccurredAt);
