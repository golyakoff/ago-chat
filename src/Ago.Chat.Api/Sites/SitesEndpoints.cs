using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Ago.Chat.Api.Auth;
using Ago.Chat.Api.Http;
using Ago.Chat.Application.UseCases.RegisterSite;
using Ago.Chat.Application.UseCases.RequestSiteErasure;
using Ago.Chat.Domain;

namespace Ago.Chat.Api.Sites;

/// <summary>
/// `10-02`: the one bootstrap endpoint `10-01`'s `RequireKeycloakIdentity` policy exists to gate -
/// its own file, not folded into <c>OperatorsEndpoints</c>, since this is about the `Site` resource
/// itself and, unlike every other route in this codebase, is reachable by a caller who is not yet an
/// operator of anything.
/// </summary>
public static class SitesEndpoints
{
    public static void MapSitesEndpoints(this WebApplication app)
    {
        // `adr/0027`: never RequireOperatorIdentity - the caller has no OperatorId claim yet by
        // definition (that is exactly what this endpoint is about to create).
        //
        // `12-04` briefly added a second policy here, `AuthorizationPolicies.NotThePlatformOwner`,
        // refusing the platform owner outright. `12-05` removed it (`adr/0063`, "Reversed in
        // 12-05"): the trap that item found was the console *routing* an owner to a form they never
        // asked for, and that is what the routing fix closed. Being the platform owner and running a
        // tenant are orthogonal (`adr/0063`), so refusing here made the axes exclusive at exactly one
        // endpoint, contradicting the ADR the same item wrote. Filling in a site name and an embed
        // origin is not something anybody does by accident, and this identity gets exactly the
        // ordinary caller's outcome - including `10-02`'s one-registration-per-identity `409` on a
        // second attempt.
        app.MapPost("/api/v1/sites", HandleRegisterSiteAsync)
            .RequireAuthorization("RequireKeycloakIdentity");

        // `16-02`: siteId from the route, not `user.GetSiteId()` - the same convention
        // `WidgetConfigEndpoints` already established for a site-scoped, operator-only admin action:
        // an operator's own active-site claim is not necessarily the site being erased (`13-07`'s
        // multi-tenancy means one identity can hold operator rows on several sites), and
        // `PermissionChecker.HasPermissionAsync` checks this specific `(OperatorId, SiteId)` pair
        // regardless of which site the caller's token happens to be scoped to right now.
        app.MapPost("/api/v1/sites/{siteId:guid}/erase", HandleEraseSiteAsync)
            .RequireAuthorization("RequireOperatorIdentity");
    }

    private static async Task<IResult> HandleRegisterSiteAsync(
        RegisterSiteRequest request,
        RegisterSiteHandler handler,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        // Read directly off the validated token's `sub` - this caller has no OperatorId/SiteId claim
        // (OperatorIdentityClaimsTransformation added nothing, by definition of reaching this
        // endpoint at all), so ClaimsPrincipalExtensions.GetOperatorId()/GetSiteId() are not usable
        // here the way every other operator-only endpoint in this codebase already relies on them.
        var externalSubjectId = httpContext.User.FindFirstValue(JwtRegisteredClaimNames.Sub);
        if (string.IsNullOrEmpty(externalSubjectId))
        {
            // Authentication already required a valid token (RequireKeycloakIdentity); a
            // Keycloak-issued token missing its own `sub` would mean Keycloak itself is
            // misconfigured, not a caller error - a 500 is the honest response, not a 400.
            return Results.Problem(statusCode: StatusCodes.Status500InternalServerError, title: "Token carries no subject claim.");
        }

        // Best-effort - RemoteIpAddress is null for some hosting/test setups (`3-05`'s own
        // per-visitor buckets have the identical fallback question, unaddressed there because a
        // visitor's own token id was always available as the primary key); "unknown" still buckets
        // every such caller together rather than throwing, and every real deployment (`edge.md`: the
        // Gateway terminates client connections directly, no proxy hop this project does not control)
        // has a real RemoteIpAddress.
        var requestIp = httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";

        var result = await handler.HandleAsync(
            new RegisterSite(externalSubjectId, requestIp, request.SiteName, request.InitialAllowedOrigin),
            cancellationToken);

        if (result.IsFailure)
        {
            return result.Error!.Value.ToProblem(httpContext);
        }

        // `10-02`'s own Scope: Location points at a resource shape with no matching GET behind it yet
        // (no "get my site" read model exists this stage) - still a valid Location per api-design.md
        // ("POST returns 201 with a Location"), which does not require the target to already be
        // readable, only that it names the created resource. Flagged here as a real, separate gap
        // (a GET /api/v1/sites/{id} endpoint), not built speculatively by this item.
        return Results.Created(
            $"/api/v1/sites/{result.Value.SiteId}",
            new RegisterSiteResponse(result.Value.SiteId, result.Value.OperatorId));
    }

    /// <summary>
    /// `16-02`: `POST /api/v1/sites/{siteId}/erase` - stamps `sites.erasure_requested_at` in one
    /// statement and returns immediately; no deletion happens on this request
    /// (`RequestSiteErasureHandler`'s own remarks). `202 Accepted`, not `204 No Content`
    /// (`CloseConversationHandler`'s own code for a write that *is* complete when the response is
    /// sent) - the first `202` this codebase returns, because this is the first write whose effect
    /// genuinely is not yet visible when the response is sent: the request is accepted,
    /// `Ago.Chat.Worker`'s `SiteErasureJob` has not run yet, and the honest answer to "is the site gone
    /// now" is "not yet."
    /// </summary>
    private static async Task<IResult> HandleEraseSiteAsync(
        Guid siteId, RequestSiteErasureHandler handler, HttpContext httpContext, CancellationToken cancellationToken)
    {
        var user = httpContext.User;
        var result = await handler.HandleAsync(
            new RequestSiteErasure(new SiteId(siteId), user.GetOperatorId()), cancellationToken);

        return result.IsFailure ? result.Error!.Value.ToProblem(httpContext) : Results.Accepted();
    }

    public sealed record RegisterSiteRequest(string SiteName, string InitialAllowedOrigin);

    public sealed record RegisterSiteResponse(Guid SiteId, Guid OperatorId);
}
