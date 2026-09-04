using Ago.Chat.Application.Abstractions;
using Ago.Chat.Application.UseCases.GetSiteConfigById;
using Ago.Chat.Contracts;
using Ago.Chat.Domain;
using Ago.Platform.Kernel;

namespace Ago.Chat.Application.UseCases.GetMyPermissions;

/// <summary>
/// `5-08`: closes a real gap found while building this item - the console has no other way to learn
/// which permissions the signed-in operator holds, and needs that to decide whether to show the admin
/// nav item or the attachment-delete action, since Keycloak's own token carries no `OperatorId`/role/
/// permission claims at all (`authorization.md`'s "resolve at request time" shape,
/// `OperatorIdentityClaimsTransformation`'s own remarks). A pure query, no Domain step, same "no
/// business invariant to enforce, just a read" shape `GetOperatorQueueHandler` already established.
///
/// `11-11`(console): also resolves the active site's own `Locale`, through `GetSiteConfigByIdHandler`
/// - the same cache-aside port `SendOfflineAutoReplyHandler` already reuses for the identical site,
/// rather than a second cached lookup of the same row. A cache miss (the site vanished between the
/// operator's token being issued and this call - the same theoretical race every other reader of a
/// cached `Site` shape tolerates) falls back to `Locale.En`, never a thrown exception over a
/// display-language nicety this response's callers already treat as best-effort.
///
/// `23-21`: also resolves the caller's own site's enabled modules, through the same
/// <see cref="IEnabledModuleReadStore.GetForSiteAsync"/> port `23-01`'s
/// <c>ListEnabledModulesForSiteHandler</c> already uses - never the
/// route-supplied <c>siteId</c> that handler takes, always <see cref="GetMyPermissions.SiteId"/>, the
/// operator claim this query was constructed from at the endpoint
/// (<c>OperatorsEndpoints.HandleGetMyPermissionsAsync</c>). That is what keeps this a read of the
/// caller's own tenant rather than a second uncontrolled cross-tenant read - the exact failure `23-01`
/// closed on the neighbouring route (see that handler's own remarks) - and it is why this method takes
/// no new permission check of its own: "what has my own tenant switched on" needs nothing beyond being
/// authenticated as an operator of it, the same reasoning this handler's own doc comment already gives
/// for the permission list beside it.
/// </summary>
public sealed class GetMyPermissionsHandler(
    IPermissionChecker permissions, GetSiteConfigByIdHandler siteConfig, IEnabledModuleReadStore moduleReadStore, IClock clock)
{
    public async Task<Result<OperatorPermissionsResponse>> HandleAsync(
        GetMyPermissions query, CancellationToken cancellationToken)
    {
        var granted = await permissions.GetPermissionsAsync(query.OperatorId, query.SiteId, cancellationToken);
        var config = await siteConfig.HandleAsync(
            new Ago.Chat.Application.UseCases.GetSiteConfigById.GetSiteConfigById(query.SiteId), cancellationToken);
        var locale = config?.WidgetLocale ?? Locale.En;
        var modules = await moduleReadStore.GetForSiteAsync(query.SiteId, clock.UtcNow, cancellationToken);
        var enabledModules = modules.Select(m => m.ModuleKey.Value).ToArray();
        return new OperatorPermissionsResponse(
            query.OperatorId.Value, query.SiteId.Value, granted, locale.ToString(), enabledModules);
    }
}
