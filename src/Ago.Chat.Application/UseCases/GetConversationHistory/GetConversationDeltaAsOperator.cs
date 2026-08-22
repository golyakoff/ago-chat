using Ago.Chat.Domain;

namespace Ago.Chat.Application.UseCases.GetConversationHistory;

/// <summary>`3-03`: the operator-side equivalent of <see cref="GetConversationDeltaAsVisitor"/>.</summary>
public sealed record GetConversationDeltaAsOperator(
    ConversationId ConversationId, OperatorId RequestedBy, SiteId SiteId, int AfterSequence);
