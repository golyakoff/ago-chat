using Ago.Chat.Domain;

namespace Ago.Chat.Application.UseCases.GetVisitorRestrictionsForSite;

/// <summary>`23-69`/`23-77`: the tenant's own read of who has been muted or blocked on this site -
/// `GET /api/v1/visitor-restrictions`. The identical shape
/// <see cref="Ago.Chat.Application.UseCases.GetAccessRecordsForSite.GetAccessRecordsForSite"/> already
/// has for a sibling compliance-shaped, keyset-paginated, site-scoped read. <paramref name="Before"/>
/// <see langword="null"/> means the first page.</summary>
public sealed record GetVisitorRestrictionsForSite(SiteId SiteId, OperatorId RequestedBy, Guid? Before, int? Limit);
