using Ago.Chat.Api.Http;
using Ago.Chat.Application.Abstractions;
using Ago.Chat.Application.UseCases.AddRolePermissionsAsOwner;
using Ago.Chat.Domain;
using Ago.Platform.Kernel;

namespace Ago.Chat.Api.Owner;

/// <summary>
/// `25-76`: the platform owner's own write for "a tenant's role is missing a permission, add it" -
/// see <see cref="AddRolePermissionsAsOwner"/>'s own remarks for what this does and does not change
/// (ADD only; there is no removal route anywhere in this file or this item). A deliberately separate
/// route and file from <see cref="Operators.OperatorsEndpoints"/> - the same "`/owner/` stays the
/// platform owner's own namespace, never blurred with a site-scoped operator route" discipline every
/// other owner endpoint file in this codebase already states for itself
/// (<see cref="OwnerOperatorsEndpoints"/>'s own remarks, restated here rather than reinvented).
///
/// <para><b>Gated exclusively by <c>RequirePlatformOwner</c></b> - the entire access-control story,
/// the identical single-gate shape every other owner surface in this codebase already uses: the
/// handler this route resolves calls no <see cref="Application.Abstractions.IPermissionChecker"/>, and
/// could not (<see cref="AddRolePermissionsAsOwnerHandler"/>'s own remarks for why), which is precisely
/// why this route must never be mapped with any weaker policy.</para>
/// </summary>
public static class OwnerRolesEndpoints
{
    public static void MapOwnerRolesEndpoints(this WebApplication app)
    {
        app.MapPut(
                "/api/v1/owner/sites/{siteId:guid}/roles/{roleName}/permissions",
                HandleAddPermissionsAsync)
            .RequireAuthorization("RequirePlatformOwner");
    }

    private static async Task<IResult> HandleAddPermissionsAsync(
        Guid siteId,
        string roleName,
        AddRolePermissionsRequest request,
        AddRolePermissionsAsOwnerHandler handler,
        IRoleRepository roles,
        IAccessRecordRepository accessRecords,
        IClock clock,
        IIdGenerator idGenerator,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        var result = await handler.HandleAsync(
            new AddRolePermissionsAsOwner(new SiteId(siteId), roleName, request.Permissions),
            cancellationToken);

        if (result.IsFailure)
        {
            return result.Error!.Value.ToProblem(httpContext);
        }

        // `24-12`: resourceId is the role's own row id - resolved again here rather than threaded back
        // from the handler, the identical "the endpoint records, the handler only decides whether to
        // succeed" split every other owner write in this file's sibling endpoints already keeps
        // (OwnerModuleEndpoints.HandleGrantAsync's own remarks). A second lookup, not a redundant one:
        // the handler's own success tells this endpoint nothing about *which* row was touched beyond
        // the name already in the URL, and OwnerAccessRecorder needs the row's real id, the same
        // "name the specific row, not just the site" shape OwnerOperatorsEndpoints.HandleRestoreSeatAsync's
        // own remarks already establish for a different resource.
        var role = await roles.GetByNameAsync(new SiteId(siteId), roleName, cancellationToken);

        await OwnerAccessRecorder.RecordAsync(
            httpContext, accessRecords, clock, idGenerator, AccessRecordKind.OwnerRolePermissionsGrant,
            new SiteId(siteId), AccessRecordResourceKind.Role, role?.Id, cancellationToken);

        return Results.Ok(new AddRolePermissionsResponse(roleName, request.Permissions));
    }

    /// <summary>The body <c>PUT /api/v1/owner/sites/{siteId}/roles/{roleName}/permissions</c> takes -
    /// one or more permission values to add. See <see cref="AddRolePermissionsAsOwner"/>'s own remarks
    /// for why an unknown value refuses the whole request rather than adding only what it
    /// recognises.</summary>
    public sealed record AddRolePermissionsRequest(IReadOnlyList<string> Permissions);

    public sealed record AddRolePermissionsResponse(string RoleName, IReadOnlyList<string> Permissions);
}
