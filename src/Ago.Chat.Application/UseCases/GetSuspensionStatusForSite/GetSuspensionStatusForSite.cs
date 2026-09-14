using Ago.Chat.Domain;

namespace Ago.Chat.Application.UseCases.GetSuspensionStatusForSite;

/// <summary>
/// `25-70`: the tenant's own read of its own account's suspension state -
/// `GET /api/v1/sites/{siteId}/suspension`. The tenant-scoped sibling of
/// `Ago.Chat.Application.UseCases.ListSuspensionsForOwner.ListSuspensionsForOwner`: that query spans
/// every currently-suspended site and is gated on `RequirePlatformOwner` at the route; this one is
/// scoped to the single site named in the route and gated on <see cref="Domain.Permission.SiteConfigure"/>
/// through <see cref="Abstractions.IPermissionChecker"/>, the identical shape
/// <see cref="ListEnabledModulesForSite.ListEnabledModulesForSite"/> already established for the other
/// tenant-facing "how is my own site set up" read on this same route group.
/// </summary>
public sealed record GetSuspensionStatusForSite(OperatorId RequestedBy, SiteId SiteId);
