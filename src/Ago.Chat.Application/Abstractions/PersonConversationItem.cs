using Ago.Chat.Domain;

namespace Ago.Chat.Application.Abstractions;

/// <summary>
/// `26-269`: one row of <see cref="IConversationReadStore.GetConversationsForPersonAsync"/> - the
/// navigation the client-detail hub needs to answer "which dialog do I open for this person": the
/// active one if there is one, else the most recent. Unpaginated by design, the same reasoning
/// <see cref="IConversationReadStore.ListAllForVisitorAsync"/> already gives for itself - nobody
/// accumulates thousands of conversations, so this reads like a small, bounded, one-person-at-a-time
/// list rather than the unbounded per-site history <see cref="ConversationListPage"/> exists to page
/// through.
///
/// <para><see cref="State"/> uses the identical "active" definition
/// <see cref="IConversationRepository.GetActiveForVisitorAsync"/> already establishes in
/// production code - not <see cref="Domain.ConversationState.Closed"/> - rather than a second,
/// independently-invented rule that could drift from it. By that same construction (a visitor may
/// hold at most one non-<c>Closed</c> conversation at a time - <c>StartConversationHandler</c>'s own
/// <c>GetActiveForVisitorAsync</c> check), at most one row in a result set is ever active.</para>
///
/// <para><see cref="LastActivityAt"/> is the conversation's own last message time, falling back to
/// <see cref="StartedAt"/> for one with no messages yet - the same "no message yet" case
/// <see cref="VisitorHistoryItem"/>'s preview fields already leave null, except this field may never
/// be null: it is the sort key that makes "else the most recent" answerable without a second,
/// nullable tiebreaker on the wire.</para>
/// </summary>
public sealed record PersonConversationItem(
    ConversationId Id,
    string State,
    DateTimeOffset StartedAt,
    DateTimeOffset? ClosedAt,
    DateTimeOffset LastActivityAt);
