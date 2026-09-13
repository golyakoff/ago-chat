using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Ago.Chat.Api.Http;
using Ago.Chat.Application.UseCases.ExtendSuspensionAsOwner;
using Ago.Chat.Application.UseCases.LiftSuspensionAsOwner;
using Ago.Chat.Application.UseCases.ListSuspensionsForOwner;
using Ago.Chat.Application.UseCases.SuspendTenantAsOwner;
using Ago.Chat.Domain;

namespace Ago.Chat.Api.Owner;

/// <summary>
/// `22-08`/`adr/0166`: the platform owner's own account-wide freeze - suspend, extend, unblock, and
/// the console's own "who is currently suspended" list. A deliberately separate file from
/// <see cref="OwnerModuleEndpoints"/>, the same "own file, own Map call" discipline that file's own
/// remarks describe for <see cref="OwnerSitesEndpoints"/>'s detail route: a suspension is an act on
/// the account itself, not on one module's own entitlement, and the two are unrelated write paths that
/// happen to share a platform-owner gate.
///
/// <para><b>Gated exclusively by <c>RequirePlatformOwner</c></b> - the entire access-control story for
/// every write this file maps, the same single-gate shape every other owner surface already uses: no
/// handler this file resolves calls <c>IPermissionChecker</c>, and none could (a suspension is not a
/// tenant-scoped permission to hold).</para>
/// </summary>
public static class OwnerSuspensionEndpoints
{
    public static void MapOwnerSuspensionEndpoints(this WebApplication app)
    {
        var siteGroup = app.MapGroup("/api/v1/owner/sites/{siteId:guid}")
            .RequireAuthorization("RequirePlatformOwner");

        siteGroup.MapPost("/suspend", HandleSuspendAsync);
        siteGroup.MapPost("/suspension/extend", HandleExtendAsync);
        siteGroup.MapPost("/suspension/lift", HandleLiftAsync);

        // `docs/backlog/22-08-*.md`'s own Scope: "a console screen listing currently-suspended
        // accounts" - spans every tenant, so it is its own route rather than nested under one site's
        // own group, the identical shape `OwnerSitesEndpoints.MapOwnerEndpoints`'s own list uses.
        app.MapGet("/api/v1/owner/suspensions", HandleListAsync)
            .RequireAuthorization("RequirePlatformOwner");
    }

    private static async Task<IResult> HandleSuspendAsync(
        Guid siteId,
        SuspendTenantRequest request,
        SuspendTenantAsOwnerHandler handler,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        var performedBy = SubjectOf(httpContext);
        if (performedBy is null)
        {
            return MissingSubjectProblem();
        }

        var result = await handler.HandleAsync(
            new SuspendTenantAsOwner(new SiteId(siteId), performedBy, request.DurationMinutes, request.Reason),
            cancellationToken);

        return result.IsFailure
            ? result.Error!.Value.ToProblem(httpContext)
            : Results.Ok(new SuspensionResponse(result.Value));
    }

    private static async Task<IResult> HandleExtendAsync(
        Guid siteId,
        ExtendSuspensionRequest request,
        ExtendSuspensionAsOwnerHandler handler,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        var performedBy = SubjectOf(httpContext);
        if (performedBy is null)
        {
            return MissingSubjectProblem();
        }

        var result = await handler.HandleAsync(
            new ExtendSuspensionAsOwner(new SiteId(siteId), performedBy, request.AdditionalMinutes, request.Reason),
            cancellationToken);

        return result.IsFailure
            ? result.Error!.Value.ToProblem(httpContext)
            : Results.Ok(new SuspensionResponse(result.Value));
    }

    private static async Task<IResult> HandleLiftAsync(
        Guid siteId,
        LiftSuspensionRequest request,
        LiftSuspensionAsOwnerHandler handler,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        var performedBy = SubjectOf(httpContext);
        if (performedBy is null)
        {
            return MissingSubjectProblem();
        }

        var result = await handler.HandleAsync(
            new LiftSuspensionAsOwner(new SiteId(siteId), performedBy, request.Reason), cancellationToken);

        return result.IsFailure ? result.Error!.Value.ToProblem(httpContext) : Results.Ok();
    }

    private static async Task<IResult> HandleListAsync(
        ListSuspensionsForOwnerHandler handler, CancellationToken cancellationToken)
    {
        var suspensions = await handler.HandleAsync(new ListSuspensionsForOwner(), cancellationToken);
        return Results.Ok(new OwnerSuspensionsResponse(suspensions
            .Select(s => new OwnerSuspensionDto(
                s.SiteId.Value, s.SiteName, s.SuspendedUntil, s.LastActionBy, s.LastActionReason, s.LastActionAt))
            .ToArray()));
    }

    /// <summary>The identical "read directly off the validated token, not threaded through" shape
    /// <c>OwnerModuleEndpoints.HandleRevokeAsync</c>'s own remarks give <c>revokedBy</c> - recorded,
    /// never authorising; <c>RequirePlatformOwner</c> on the route already decided that.</summary>
    private static string? SubjectOf(HttpContext httpContext) =>
        httpContext.User.FindFirstValue(JwtRegisteredClaimNames.Sub) is { Length: > 0 } sub ? sub : null;

    private static IResult MissingSubjectProblem() =>
        Results.Problem(statusCode: StatusCodes.Status500InternalServerError, title: "Token carries no subject claim.");

    public sealed record SuspendTenantRequest(int DurationMinutes, string Reason);

    public sealed record ExtendSuspensionRequest(int AdditionalMinutes, string Reason);

    public sealed record LiftSuspensionRequest(string Reason);

    public sealed record SuspensionResponse(DateTimeOffset SuspendedUntil);

    public sealed record OwnerSuspensionDto(
        Guid SiteId, string SiteName, DateTimeOffset SuspendedUntil, string LastActionBy,
        string LastActionReason, DateTimeOffset LastActionAt);

    public sealed record OwnerSuspensionsResponse(IReadOnlyList<OwnerSuspensionDto> Suspensions);
}
