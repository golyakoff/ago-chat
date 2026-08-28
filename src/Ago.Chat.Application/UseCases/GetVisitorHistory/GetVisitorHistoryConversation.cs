using Ago.Chat.Domain;

namespace Ago.Chat.Application.UseCases.GetVisitorHistory;

/// <summary>
/// `18-07`: "open one" - reading the real message history of a *different*, past conversation of the
/// same visitor. <paramref name="ConversationId"/> is the operator's standing (the conversation they
/// are actually assigned to right now); <paramref name="HistoricalConversationId"/> is the one they
/// are asking to read. See <c>GetVisitorHistoryHandler.HandleHistoricalConversationAsOperatorAsync</c>'s
/// own remarks for why this is a distinct query from <c>GetConversationHistoryAsOperator</c> rather
/// than a second caller of it.
/// </summary>
public sealed record GetVisitorHistoryConversation(
    ConversationId ConversationId, ConversationId HistoricalConversationId, OperatorId RequestedBy,
    SiteId SiteId, int? BeforeSequence, int PageSize);
