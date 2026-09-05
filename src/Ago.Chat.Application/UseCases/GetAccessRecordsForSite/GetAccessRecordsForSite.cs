using Ago.Chat.Domain;

namespace Ago.Chat.Application.UseCases.GetAccessRecordsForSite;

/// <summary>
/// `24-12`: a tenant's own read of who accessed their data - `GET /api/v1/sites/{siteId}/access-records`.
/// The tenant-facing sibling of the platform owner's cross-tenant surfaces
/// (`ListSitesForOwner`/`GetSiteForOwner`): an ordinary, site-scoped, permission-gated query, the same
/// shape <see cref="Ago.Chat.Application.UseCases.GetSiteExportStatus.GetSiteExportStatus"/> already
/// has for a different compliance-shaped read. <paramref name="Before"/> <see langword="null"/> means
/// the first page - the same keyset convention every other paginated read in this codebase uses.
/// </summary>
public sealed record GetAccessRecordsForSite(SiteId SiteId, OperatorId RequestedBy, Guid? Before, int? Limit);
