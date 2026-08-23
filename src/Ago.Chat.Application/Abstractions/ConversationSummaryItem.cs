using Ago.Chat.Domain;

namespace Ago.Chat.Application.Abstractions;

/// <summary>
/// `5-08`: one row of <see cref="IConversationReadStore.GetAllForSiteAsync"/> - a plain projection
/// of the <c>conversations</c> table (own type, not the <see cref="Conversation"/> aggregate, the
/// same "read store returns rows, not aggregates" shape <see cref="MessageHistoryItem"/> already
/// established for the message side).
/// </summary>
public sealed record ConversationSummaryItem(
    ConversationId Id,
    VisitorId VisitorId,
    OperatorId? OperatorId,
    string State,
    DateTimeOffset CreatedAt,
    int OperatorUnreadCount);
