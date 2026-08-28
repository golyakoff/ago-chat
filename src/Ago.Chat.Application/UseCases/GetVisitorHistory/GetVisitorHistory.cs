using Ago.Chat.Domain;

namespace Ago.Chat.Application.UseCases.GetVisitorHistory;

/// <summary>`18-07`: the query behind an operator's visitor-history panel - <paramref name="ConversationId"/>
/// is the conversation the operator is currently viewing, both the source of "which visitor" and the
/// per-conversation permission anchor (see <c>GetVisitorHistoryHandler</c>'s own remarks). Same
/// <paramref name="BeforeId"/> <see langword="null"/>-means-"most recent page" convention as
/// <c>GetAllConversationsForSite</c>.</summary>
public sealed record GetVisitorHistory(
    ConversationId ConversationId, OperatorId RequestedBy, SiteId SiteId, Guid? BeforeId, int PageSize);
