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
/// </summary>
public sealed class GetMyPermissionsHandler(IPermissionChecker permissions, GetSiteConfigByIdHandler siteConfig)
{
    public async Task<Result<OperatorPermissionsResponse>> HandleAsync(
        GetMyPermissions query, CancellationToken cancellationToken)
    {
        var granted = await permissions.GetPermissionsAsync(query.OperatorId, query.SiteId, cancellationToken);
        var config = await siteConfig.HandleAsync(
            new Ago.Chat.Application.UseCases.GetSiteConfigById.GetSiteConfigById(query.SiteId), cancellationToken);
        var locale = config?.WidgetLocale ?? Locale.En;
        return new OperatorPermissionsResponse(query.OperatorId.Value, query.SiteId.Value, granted, locale.ToString());
    }
}
