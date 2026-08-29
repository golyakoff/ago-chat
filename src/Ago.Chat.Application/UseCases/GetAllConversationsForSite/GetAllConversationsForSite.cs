using Ago.Chat.Domain;

namespace Ago.Chat.Application.UseCases.GetAllConversationsForSite;

/// <summary>`5-08`: the admin/supervisor view's own query - <paramref name="BeforeId"/>
/// <see langword="null"/> means "most recent page" (same convention `GetConversationHistoryAsOperator`'s
/// `BeforeSequence` already uses). <paramref name="Tag"/>: `18-04`'s own list filter, pushed into
/// <see cref="Application.Abstractions.IConversationReadStore.GetAllForSiteAsync"/>'s own query rather
/// than filtered in memory afterward - see that method's own remarks on why this read, unlike
/// `GetOperatorQueueHandler`'s two, is genuinely paginated and cannot be filtered after the
/// page.</summary>
public sealed record GetAllConversationsForSite(
    OperatorId RequestedBy, SiteId SiteId, Guid? BeforeId, int PageSize, TagId? Tag = null);
