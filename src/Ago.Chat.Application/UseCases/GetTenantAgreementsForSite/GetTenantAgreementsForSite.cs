using Ago.Chat.Domain;

namespace Ago.Chat.Application.UseCases.GetTenantAgreementsForSite;

/// <summary>
/// `23-52`: `GET /api/v1/sites/{siteId}/agreements` - a tenant's own read of what their account
/// accepted from AGO, and when. The tenant-facing sibling of <c>GetAccessRecordsForSite</c>
/// (`24-12`)/<c>GetContactRevealsForSite</c> (`23-11`): an ordinary, site-scoped, permission-gated
/// query, not a new mechanism - see <see cref="GetTenantAgreementsForSiteHandler"/>'s own remarks for
/// why this item builds no second acceptance store or recording path.
/// </summary>
public sealed record GetTenantAgreementsForSite(SiteId SiteId, OperatorId RequestedBy);
