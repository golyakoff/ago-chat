namespace Ago.Chat.Application.Abstractions;

/// <summary>
/// `26-114`: one row of <see cref="IConversationReadStore.GetVisitorSummaryAsync"/> - the visitor's own
/// <see cref="Domain.Visitor.FirstSeenAt"/> plus how many conversations they have had on this site,
/// <b>including</b> the one currently open. See that method's own remarks for why one query answers
/// both facts together, and <c>Contracts.VisitorSummaryResponse</c>'s own remarks for how this count
/// relates to <see cref="VisitorHistoryPage"/>'s own (current-conversation-excluding) list length.
/// </summary>
public sealed record VisitorSummaryItem(DateTimeOffset FirstSeenAt, int ConversationCount);
