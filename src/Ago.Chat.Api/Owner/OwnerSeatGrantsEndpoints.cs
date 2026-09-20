using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Ago.Chat.Api.Http;
using Ago.Chat.Application.Abstractions;
using Ago.Chat.Application.UseCases;
using Ago.Chat.Application.UseCases.GetOwnerSeatSummary;
using Ago.Chat.Application.UseCases.GrantOwnerSeatsAsOwner;
using Ago.Chat.Domain;
using Ago.Platform.Kernel;

namespace Ago.Chat.Api.Owner;

/// <summary>
/// `25-181`: the platform owner's own hand-granted seat extra - "Добавить сверх тарифа", the owner
/// console's own capability for granting a tenant 1-5 extra Operator or Administrator seats by hand,
/// with a reason and an optional expiry. A deliberate sibling to <see cref="OwnerOperatorsEndpoints"/>
/// rather than an addition to it - see that file's own remarks for why: its own minimal integration
/// test host registers only what its one handler needs, and a second route needing a second handler
/// that host never registers breaks ASP.NET's own endpoint-metadata inference for the whole file, not
/// only the new route.
///
/// <para><b>Gated exclusively by <c>RequirePlatformOwner</c></b> - the identical single-gate shape
/// every other owner surface in this codebase already uses: neither handler this file resolves calls
/// <see cref="Application.Abstractions.IPermissionChecker"/>, and could not
/// (<see cref="GrantOwnerSeatsAsOwnerHandler"/>'s own remarks for why).</para>
/// </summary>
public static class OwnerSeatGrantsEndpoints
{
    public static void MapOwnerSeatGrantsEndpoints(this WebApplication app)
    {
        app.MapGet("/api/v1/owner/sites/{siteId:guid}/seat-summary", HandleGetSeatSummaryAsync)
            .RequireAuthorization("RequirePlatformOwner");

        app.MapPost("/api/v1/owner/sites/{siteId:guid}/seat-grants", HandleGrantSeatsAsync)
            .RequireAuthorization("RequirePlatformOwner");
    }

    /// <summary>`25-181`: the owner console's own "Пользователи" summary line - held/limit for both
    /// seeded roles, each limit already including the platform owner's own live grant.</summary>
    private static async Task<IResult> HandleGetSeatSummaryAsync(
        Guid siteId, GetOwnerSeatSummaryHandler handler, HttpContext httpContext, CancellationToken cancellationToken)
    {
        var result = await handler.HandleAsync(new GetOwnerSeatSummary(new SiteId(siteId)), cancellationToken);

        return result.IsFailure
            ? result.Error!.Value.ToProblem(httpContext)
            : Results.Ok(result.Value);
    }

    /// <summary>`25-181`: "Добавить сверх тарифа" - the platform owner granting 1-5 extra seats of one
    /// role, optionally with an expiry, always with a reason. See <see cref="GrantOwnerSeatsAsOwnerHandler"/>'s
    /// own remarks for why this carries no <see cref="Application.Abstractions.IPermissionChecker"/>
    /// check.</summary>
    private static async Task<IResult> HandleGrantSeatsAsync(
        Guid siteId,
        GrantOwnerSeatsRequest request,
        GrantOwnerSeatsAsOwnerHandler handler,
        IAccessRecordRepository accessRecords,
        IClock clock,
        IIdGenerator idGenerator,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        // `23-13`'s own precedent, restated for this write: read directly off the validated token's
        // `sub` - RequirePlatformOwner on this route already decided the caller may act, this only
        // decides who to record having done it (RestoreOperatorSeatAsOwnerHandler's own identical
        // remarks, `OwnerOperatorsEndpoints`).
        var grantedBy = httpContext.User.FindFirstValue(JwtRegisteredClaimNames.Sub);
        if (string.IsNullOrEmpty(grantedBy))
        {
            return Results.Problem(statusCode: StatusCodes.Status500InternalServerError, title: "Token carries no subject claim.");
        }

        // `25-181`: the role travels as a plain string on the wire ("Operator"/"Administrator") - the
        // same shape every other role-naming field in this codebase's own API already uses (e.g.
        // OwnerRolesEndpoints' own `{roleName}` route segment), never a bare enum ordinal - parsed here,
        // at the boundary, rather than asking Domain/Application to know about wire representations.
        if (!Enum.TryParse<OwnerSeatGrantRole>(request.Role, ignoreCase: true, out var role))
        {
            return ConversationErrors.OwnerSeatGrantRoleInvalid(request.Role).ToProblem(httpContext);
        }

        var result = await handler.HandleAsync(
            new GrantOwnerSeatsAsOwner(new SiteId(siteId), role, request.Quantity, grantedBy, request.Reason, request.ExpiresAt),
            cancellationToken);

        if (result.IsFailure)
        {
            return result.Error!.Value.ToProblem(httpContext);
        }

        // `24-12`: no resourceId - like OwnerModuleQuantityGrant, this row is named by (SiteId, Role)
        // rather than a synthetic id (Domain.OwnerSeatGrant's own remarks), so there is no id a second
        // lookup would buy this record.
        await OwnerAccessRecorder.RecordAsync(
            httpContext, accessRecords, clock, idGenerator, AccessRecordKind.OwnerSeatGrant,
            new SiteId(siteId), AccessRecordResourceKind.OwnerSeatGrant, resourceId: null, cancellationToken);

        return Results.Ok();
    }

    /// <summary>The body `POST .../seat-grants` takes - <see cref="GrantOwnerSeatsAsOwner"/>'s own
    /// remarks give the full shape (1-5, required reason, nullable expiry). <see cref="Role"/> is a
    /// plain string ("Operator"/"Administrator"), parsed in <see cref="HandleGrantSeatsAsync"/> - the
    /// same "the wire carries values, the client does not derive them, and the server parses at the
    /// boundary" shape every other role-naming field in this codebase's own API already uses.</summary>
    public sealed record GrantOwnerSeatsRequest(string Role, int Quantity, string Reason, DateTimeOffset? ExpiresAt);
}
