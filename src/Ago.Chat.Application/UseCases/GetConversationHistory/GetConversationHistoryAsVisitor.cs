using Ago.Chat.Domain;

namespace Ago.Chat.Application.UseCases.GetConversationHistory;

public sealed record GetConversationHistoryAsVisitor(
    ConversationId ConversationId, VisitorId RequestedBy, int? BeforeSequence, int PageSize);
