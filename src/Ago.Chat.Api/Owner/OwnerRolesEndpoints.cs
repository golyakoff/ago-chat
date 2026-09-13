using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Ago.Chat.Api.Http;
using Ago.Chat.Application.Abstractions;
using Ago.Chat.Application.UseCases.AddRolePermissionsAsOwner;
using Ago.Chat.Application.UseCases.RemoveRolePermissionsAsOwner;
using Ago.Chat.Domain;
using Ago.Platform.Kernel;

namespace Ago.Chat.Api.Owner;

/// <summary>
/// `25-76`/`25-77`: the platform owner's own writes for "a tenant's role is missing a permission, add
/// it" and its mirror, "the owner may take one away, no magic roles" - see
/// <see cref="AddRolePermissionsAsOwner"/>/<see cref="RemoveRolePermissionsAsOwner"/>'s own remarks for
/// what each direction does and does not change. A deliberately separate route and file from
/// <see cref="Operators.OperatorsEndpoints"/> - the same "`/owner/` stays the platform owner's own
/// namespace, never blurred with a site-scoped operator route" discipline every other owner endpoint
/// file in this codebase already states for itself
/// (<see cref="OwnerOperatorsEndpoints"/>'s own remarks, restated here rather than reinvented).
///
/// <para><b>Gated exclusively by <c>RequirePlatformOwner</c></b> - the entire access-control story,
/// the identical single-gate shape every other owner surface in this codebase already uses: neither
/// handler this file resolves calls <see cref="Application.Abstractions.IPermissionChecker"/>, and
/// could not (<see cref="AddRolePermissionsAsOwnerHandler"/>'s own remarks for why), which is precisely
/// why these routes must never be mapped with any weaker policy.</para>
///
/// <para><b>`25-77`: `DELETE` on the identical resource the `PUT` above already names, not a
/// distinctly-named route.</b> This codebase already has a real `DELETE`-with-body precedent -
/// <see cref="OwnerModuleEndpoints.HandleRevokeAsync"/>'s own `[FromBody] RevokeModuleAsOwnerRequest`,
/// found necessary by that item's own integration test ("`DELETE` does not allow an inferred body
/// parameter") - reused here rather than inventing a second shape (a `POST .../permissions/remove`,
/// say) for what is, structurally, the same act as the grant `PUT` one HTTP verb over: both name the
/// same role's own permission set, one widening it, one narrowing it. `Permissions` and `Reason` travel
/// together in the request body because a reason is exactly as much a part of "what changed and why" as
/// the permissions themselves - splitting them across a body and a query string would buy nothing.</para>
/// </summary>
public static class OwnerRolesEndpoints
{
    public static void MapOwnerRolesEndpoints(this WebApplication app)
    {
        app.MapPut(
                "/api/v1/owner/sites/{siteId:guid}/roles/{roleName}/permissions",
                HandleAddPermissionsAsync)
            .RequireAuthorization("RequirePlatformOwner");

        app.MapDelete(
                "/api/v1/owner/sites/{siteId:guid}/roles/{roleName}/permissions",
                HandleRemovePermissionsAsync)
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

    /// <summary>`25-77`'s own mirror of <see cref="HandleAddPermissionsAsync"/> - the caller's identity
    /// has to reach the domain row itself (<c>RolePermissionRemovalOverrideEntity.RemovedBy</c>), the
    /// identical "read directly off the validated token's `sub`, not threaded through" reasoning
    /// <see cref="OwnerModuleEndpoints.HandleSetUnconditionalGrantAsync"/>'s own remarks give for the
    /// identical claim read - restated here because this read gates what gets stored on the override
    /// row, not merely what gets recorded alongside it in <c>access_records</c>.</summary>
    private static async Task<IResult> HandleRemovePermissionsAsync(
        Guid siteId,
        string roleName,
        [Microsoft.AspNetCore.Mvc.FromBody] RemoveRolePermissionsRequest request,
        RemoveRolePermissionsAsOwnerHandler handler,
        IRoleRepository roles,
        IAccessRecordRepository accessRecords,
        IClock clock,
        IIdGenerator idGenerator,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        var removedBy = httpContext.User.FindFirstValue(JwtRegisteredClaimNames.Sub);
        if (string.IsNullOrEmpty(removedBy))
        {
            return Results.Problem(statusCode: StatusCodes.Status500InternalServerError, title: "Token carries no subject claim.");
        }

        var result = await handler.HandleAsync(
            new RemoveRolePermissionsAsOwner(
                new SiteId(siteId), roleName, request.Permissions, removedBy, request.Reason),
            cancellationToken);

        if (result.IsFailure)
        {
            return result.Error!.Value.ToProblem(httpContext);
        }

        var role = await roles.GetByNameAsync(new SiteId(siteId), roleName, cancellationToken);

        await OwnerAccessRecorder.RecordAsync(
            httpContext, accessRecords, clock, idGenerator, AccessRecordKind.OwnerRolePermissionsRemoval,
            new SiteId(siteId), AccessRecordResourceKind.Role, role?.Id, cancellationToken);

        return Results.Ok(new RemoveRolePermissionsResponse(roleName, request.Permissions));
    }

    /// <summary>The body <c>PUT /api/v1/owner/sites/{siteId}/roles/{roleName}/permissions</c> takes -
    /// one or more permission values to add. See <see cref="AddRolePermissionsAsOwner"/>'s own remarks
    /// for why an unknown value refuses the whole request rather than adding only what it
    /// recognises.</summary>
    public sealed record AddRolePermissionsRequest(IReadOnlyList<string> Permissions);

    public sealed record AddRolePermissionsResponse(string RoleName, IReadOnlyList<string> Permissions);

    /// <summary>The body <c>DELETE /api/v1/owner/sites/{siteId}/roles/{roleName}/permissions</c> takes -
    /// mirrors <see cref="AddRolePermissionsRequest"/> plus the reason `25-77`'s own "Answered" requires
    /// on every removal, unconditionally (never optional the way
    /// <see cref="OwnerModuleEndpoints.RevokeModuleAsOwnerRequest.Reason"/> only applies once
    /// <c>Force</c> is set).</summary>
    public sealed record RemoveRolePermissionsRequest(IReadOnlyList<string> Permissions, string Reason);

    public sealed record RemoveRolePermissionsResponse(string RoleName, IReadOnlyList<string> Permissions);
}
