namespace Ago.Chat.Contracts;

/// <summary>
/// `adr/0186` S1: the analytics conversion-report rollup's own input
/// (`docs/design/analytics-precompute.md` §4.1/§7) - an operator recorded (or overwrote) this
/// conversation's outcome. <see cref="Outcome"/> is the new value only; a later
/// <see cref="ConversationOutcomeRecorded"/> for the same <see cref="ConversationId"/>
/// <b>supersedes</b> an earlier one rather than adding to it (`Conversation.SetOutcome`'s own remarks:
/// "the second call overwrites the first") - the aggregator recomputes the whole day from deduplicated
/// raw, so this is a plain fact stream, never a delta the consumer must reconcile itself.
///
/// <para>Mapped from the domain event <c>Ago.Chat.Domain.ConversationOutcomeSet</c> - named
/// differently on purpose, the same domain-event/contract split <c>ConversationClosed</c>/
/// <c>ConversationEnded</c> already established, so <c>ConversationOutcomeRecordedMapper</c> can bring
/// both types into scope with no alias needed.</para>
/// </summary>
public sealed record ConversationOutcomeRecorded(
    Guid ConversationId, Guid SiteId, string Outcome, DateTimeOffset OccurredAt, Guid CorrelationId);
