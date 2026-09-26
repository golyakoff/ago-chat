namespace Ago.Chat.Domain;

/// <summary>
/// `adr/0186` S1: raised by <see cref="Conversation.SetOutcome"/> now that a real consumer exists -
/// analytics needs to know when an outcome was recorded, to feed the conversion-report rollup
/// (`docs/design/analytics-precompute.md` §4.1). Named <c>ConversationOutcomeSet</c>, not
/// <c>ConversationOutcomeRecorded</c>, deliberately different from the integration event
/// <c>Ago.Chat.Contracts.ConversationOutcomeRecorded</c> it maps to - the same
/// domain-event/contract naming split <c>ConversationClosed</c>/<c>ConversationEnded</c> already
/// established, so <c>ConversationOutcomeRecordedMapper</c> can bring both types into scope with no
/// alias needed (a real `using X = Y;` alias is never the right fix for this - the type gets renamed
/// instead).
/// </summary>
public sealed record ConversationOutcomeSet(
    ConversationId ConversationId, SiteId SiteId, ConversationOutcome Outcome, DateTimeOffset OccurredAt) : IDomainEvent;
