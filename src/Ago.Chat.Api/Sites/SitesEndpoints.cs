using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Ago.Chat.Api.Http;
using Ago.Chat.Application.UseCases.RegisterSite;

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
        app.MapPost("/api/v1/sites", HandleRegisterSiteAsync)
            .RequireAuthorization("RequireKeycloakIdentity");
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

    public sealed record RegisterSiteRequest(string SiteName, string InitialAllowedOrigin);

    public sealed record RegisterSiteResponse(Guid SiteId, Guid OperatorId);
}
