using Ago.Chat.Domain;

namespace Ago.Chat.Application.UseCases.GetConversationHistory;

public sealed record GetConversationHistoryAsOperator(
    ConversationId ConversationId, OperatorId RequestedBy, SiteId SiteId, int? BeforeSequence, int PageSize);
