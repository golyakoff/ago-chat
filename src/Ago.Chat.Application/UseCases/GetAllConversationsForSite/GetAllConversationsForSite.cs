using Ago.Chat.Domain;

namespace Ago.Chat.Application.UseCases.GetAllConversationsForSite;

/// <summary>`5-08`: the admin/supervisor view's own query - <paramref name="BeforeId"/>
/// <see langword="null"/> means "most recent page" (same convention `GetConversationHistoryAsOperator`'s
/// `BeforeSequence` already uses).</summary>
public sealed record GetAllConversationsForSite(
    OperatorId RequestedBy, SiteId SiteId, Guid? BeforeId, int PageSize);
