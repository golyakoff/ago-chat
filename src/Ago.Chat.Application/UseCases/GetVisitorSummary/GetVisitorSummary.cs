using Ago.Chat.Domain;

namespace Ago.Chat.Application.UseCases.GetVisitorSummary;

/// <summary>`26-114`: the query behind the contact-detail panel's own header facts -
/// <paramref name="ConversationId"/> is the conversation the operator is currently viewing, both the
/// source of "which visitor" and the per-conversation permission anchor, the identical role it plays in
/// <c>GetVisitorHistory.GetVisitorHistoryHandler</c>'s own sibling query.</summary>
public sealed record GetVisitorSummary(ConversationId ConversationId, OperatorId RequestedBy, SiteId SiteId);
