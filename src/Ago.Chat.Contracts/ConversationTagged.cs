namespace Ago.Chat.Contracts;

/// <summary>
/// `adr/0186` S1: the analytics tag-breakdown rollup's own input (`docs/design/analytics-precompute.md`
/// §4.1/§7) - a tag was newly applied to this conversation. Published from
/// <c>Ago.Chat.Infrastructure.Postgres.TagRepository.AddToConversationAsync</c>, the write's own
/// adapter, not from <c>Ago.Chat.Application.UseCases.TagConversation.TagConversationHandler</c>
/// directly - tags have no aggregate of their own to route a domain event through
/// (<c>ITagRepository</c>'s own remarks: "no lifecycle of their own separate from the tag and the
/// conversation they join"), so the outbox write and the raw
/// <c>insert ... on conflict do nothing</c> that changes state are staged in one explicit transaction
/// at the one place that issues both - the same "the adapter owns the transaction, not the caller"
/// shape <c>Ago.Chat.Infrastructure.Postgres.ModuleQuantityGrantStore</c> already establishes for a
/// write with no aggregate either.
///
/// <para><b>Never published for a no-op.</b> <c>AddToConversationAsync</c>'s own contract is
/// idempotent - tagging an already-tagged conversation changes nothing - so the adapter only enqueues
/// this event when the insert actually affected a row; a repeat call must not fabricate a second
/// "tagged" fact analytics would double-count.</para>
/// </summary>
public sealed record ConversationTagged(
    Guid ConversationId, Guid SiteId, Guid TagId, DateTimeOffset OccurredAt, Guid CorrelationId);
