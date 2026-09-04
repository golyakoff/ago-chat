using Ago.Chat.Domain;

namespace Ago.Chat.Application.UseCases.GetSiteForOwner;

/// <summary>
/// `23-14`: the platform owner's per-tenant detail read - `GET /api/v1/owner/sites/{siteId}`. The
/// cross-tenant sibling of `ListSitesForOwner`, not of `23-01`'s `ListEnabledModulesForSite`: both of
/// those take a `SiteId`, but `ListEnabledModulesForSite` also carries a `RequestedBy` its handler
/// checks through `IPermissionChecker` (a tenant's own operator, reading their own site), while this
/// record carries no requester at all, the same shape `ListSitesForOwner` has for the identical
/// reason - the fact that authorizes this call is `12-01`'s `RequirePlatformOwner` policy at the
/// route, a Keycloak realm role `Ago.Chat.Application` has no port to see, not a row in
/// `roles`/`operator_roles` this layer could check. See
/// <see cref="GetSiteForOwnerHandler"/>'s own remarks for the full argument.
/// </summary>
public sealed record GetSiteForOwner(SiteId SiteId);
