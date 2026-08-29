using Ago.Chat.Domain;

namespace Ago.Chat.Application.Abstractions;

/// <summary>
/// `18-01`: one full-text hit - the message that matched, and just enough about its conversation for
/// an operator to recognise it in a results list before clicking through (the same "list is thin,
/// opening reads the real thing" split <see cref="ConversationSummaryItem"/>'s own remarks establish
/// for the admin's site-wide list; opening a hit re-uses the existing history read, positioned at
/// <see cref="MessageId"/>, rather than this endpoint trying to carry a transcript of its own).
/// <see cref="ConversationState"/> is a plain string, matching <see cref="ConversationSummaryItem.State"/>'s
/// own convention for a read-store projection over this column.
/// </summary>
/// <summary><see cref="Sequence"/> is what lets the console open a hit at the right position rather
/// than only at the right conversation: <c>IConversationReadStore.GetHistoryAsync</c>'s own
/// <c>beforeSequence</c> keyset cursor is the one thing that can land a history page containing this
/// exact message, and it is keyed on <see cref="Sequence"/>, never on <see cref="MessageId"/> - the
/// same reason <see cref="MessageHistoryItem"/> itself carries <c>Sequence</c> alongside its
/// id.</summary>
public sealed record ConversationSearchResultItem(
    ConversationId ConversationId,
    MessageId MessageId,
    int Sequence,
    string MatchedBody,
    MessageAuthorKind AuthorKind,
    DateTimeOffset CreatedAt,
    string ConversationState);
