using Ago.Chat.Domain;

namespace Ago.Chat.Application.UseCases.ListEnabledModulesForSite;

/// <summary>
/// `23-01`: the console's own read of a site's enabled modules - `GET /api/v1/sites/{siteId}/modules`.
/// Split out of <see cref="ModuleEndpoints.ModuleEndpoints.MapModuleEndpoints"/>, which used to call
/// <c>IEnabledModuleReadStore.GetForSiteAsync</c> straight from the endpoint with the route's
/// <c>siteId</c> compared against nothing - the one verb on that route group with no permission
/// check, while its four siblings (`PUT`, `DELETE`, `/rotate`, `/verify`) each dispatch to a handler
/// that gates on <see cref="Permission.SiteConfigure"/>. See
/// <see cref="ListEnabledModulesForSiteHandler"/>'s own remarks for why this read is gated on the same
/// permission as the writes beside it.
/// </summary>
public sealed record ListEnabledModulesForSite(OperatorId RequestedBy, SiteId SiteId);
