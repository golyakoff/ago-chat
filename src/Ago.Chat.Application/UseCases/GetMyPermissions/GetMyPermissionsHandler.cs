using Ago.Chat.Application.Abstractions;
using Ago.Chat.Contracts;
using Ago.Platform.Kernel;

namespace Ago.Chat.Application.UseCases.GetMyPermissions;

/// <summary>
/// `5-08`: closes a real gap found while building this item - the console has no other way to learn
/// which permissions the signed-in operator holds, and needs that to decide whether to show the admin
/// nav item or the attachment-delete action, since Keycloak's own token carries no `OperatorId`/role/
/// permission claims at all (`authorization.md`'s "resolve at request time" shape,
/// `OperatorIdentityClaimsTransformation`'s own remarks). A pure query, no Domain step, same "no
/// business invariant to enforce, just a read" shape `GetOperatorQueueHandler` already established.
/// </summary>
public sealed class GetMyPermissionsHandler(IPermissionChecker permissions)
{
    public async Task<Result<OperatorPermissionsResponse>> HandleAsync(
        GetMyPermissions query, CancellationToken cancellationToken)
    {
        var granted = await permissions.GetPermissionsAsync(query.OperatorId, query.SiteId, cancellationToken);
        return new OperatorPermissionsResponse(query.OperatorId.Value, query.SiteId.Value, granted);
    }
}
