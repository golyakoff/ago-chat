using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Ago.Chat.Application.UseCases.ListMyTenancies;
using Ago.Chat.Contracts;

namespace Ago.Chat.Api.Me;

/// <summary>
/// `13-07`/`adr/0068`: routes about the calling *identity* rather than about an already-resolved
/// operator or site - `GET /api/v1/me/tenancies` is the first of these, and its own file for the same
/// reason `SitesEndpoints` is its own file rather than folded into `OperatorsEndpoints`: it is
/// reachable by a caller with no `OperatorId`/`SiteId` claim pair yet (an identity with zero or
/// several tenancies fails `RequireOperatorIdentity` - `ResolveOperatorIdentityHandler`'s own doc
/// comment), gated by the same `RequireKeycloakIdentity` policy `SitesEndpoints`'s bootstrap endpoint
/// uses, for the identical reason.
/// </summary>
public static class MeEndpoints
{
    public static void MapMeEndpoints(this WebApplication app)
    {
        app.MapGet("/api/v1/me/tenancies", HandleListMyTenanciesAsync)
            .RequireAuthorization("RequireKeycloakIdentity");
    }

    private static async Task<IResult> HandleListMyTenanciesAsync(
        ListMyTenanciesHandler handler, HttpContext httpContext, CancellationToken cancellationToken)
    {
        // Read directly off the validated token's `sub`, never from the request - the identical rule
        // `SitesEndpoints.HandleRegisterSiteAsync` already follows and for the same reason: this
        // caller may have no `OperatorId`/`SiteId` claim pair yet, so
        // `ClaimsPrincipalExtensions.GetOperatorId`/`GetSiteId` are not usable here.
        var externalSubjectId = httpContext.User.FindFirstValue(JwtRegisteredClaimNames.Sub);
        if (string.IsNullOrEmpty(externalSubjectId))
        {
            // Same reasoning as SitesEndpoints: RequireKeycloakIdentity already required a valid
            // token, so a missing `sub` means Keycloak itself is misconfigured, not a caller error.
            return Results.Problem(statusCode: StatusCodes.Status500InternalServerError, title: "Token carries no subject claim.");
        }

        var tenancies = await handler.HandleAsync(new ListMyTenanciesQuery(externalSubjectId), cancellationToken);
        return Results.Ok(new TenanciesResponse(
            [.. tenancies.Select(t => new TenancyDto(t.SiteId, t.SiteName))]));
    }
}
