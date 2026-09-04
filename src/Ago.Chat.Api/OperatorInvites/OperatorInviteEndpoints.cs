using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Ago.Chat.Api.Auth;
using Ago.Chat.Api.Http;
using Ago.Chat.Application.UseCases.CreateOperatorInvite;
using Ago.Chat.Application.UseCases.RedeemOperatorInvite;
using Ago.Chat.Domain;

namespace Ago.Chat.Api.OperatorInvites;

/// <summary>
/// `13-01`: `10-02`'s own Out of scope gap, closed - a real way for an existing operator to add a
/// second, third, ... operator to their site. Two routes with deliberately different gates, mirroring
/// `SitesEndpoints`' own split between a caller who already administers a site and one who does not
/// exist as an operator yet:
///
/// <list type="bullet">
/// <item><c>POST /api/v1/sites/{siteId}/operator-invites</c> - `RequireOperatorIdentity`, the same
/// policy `WebhookEndpoints`' own routes use; `Permission.SiteManageOperators` is checked inside the
/// handler (`IPermissionChecker`, no new mechanism), not at this route's own policy layer.</item>
/// <item><c>POST /api/v1/operator-invites/redeem</c> - `RequireKeycloakIdentity`, never
/// `RequireOperatorIdentity`, for the identical reason `SitesEndpoints`' bootstrap route uses it: the
/// caller has no `OperatorId` claim yet by definition (`10-01`'s own precedent, reused verbatim).</item>
/// </list>
/// </summary>
public static class OperatorInviteEndpoints
{
    public static void MapOperatorInviteEndpoints(this WebApplication app)
    {
        app.MapPost("/api/v1/sites/{siteId:guid}/operator-invites", HandleCreateAsync)
            .RequireAuthorization("RequireOperatorIdentity");

        app.MapPost("/api/v1/operator-invites/redeem", HandleRedeemAsync)
            .RequireAuthorization("RequireKeycloakIdentity");
    }

    private static async Task<IResult> HandleCreateAsync(
        Guid siteId,
        CreateOperatorInviteRequest request,
        CreateOperatorInviteHandler handler,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        var user = httpContext.User;
        var result = await handler.HandleAsync(
            new CreateOperatorInvite(user.GetOperatorId(), new SiteId(siteId), request.RoleName), cancellationToken);

        if (result.IsFailure)
        {
            return result.Error!.Value.ToProblem(httpContext);
        }

        // `201`, not `200` - a new operator_invites row was created, matching
        // RegisterWebhookEndpointHandler's own "shown exactly once" precedent for a different generated
        // bearer secret. No Location: like `10-02`'s own bootstrap endpoint, there is no matching GET
        // for a single invite yet (this item's own Out of scope names no console/read surface as
        // needed) - flagged here rather than built speculatively, the identical gap SitesEndpoints'
        // own remarks already accept for the same reason.
        return Results.Created(
            $"/api/v1/sites/{siteId}/operator-invites/{result.Value.OperatorInviteId}",
            new CreateOperatorInviteResponse(result.Value.OperatorInviteId, result.Value.Code, result.Value.ExpiresAt));
    }

    private static async Task<IResult> HandleRedeemAsync(
        RedeemOperatorInviteRequest request,
        RedeemOperatorInviteHandler handler,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        // Read directly off the validated token's `sub`, the same as SitesEndpoints.HandleRegisterSiteAsync
        // - this caller has no OperatorId/SiteId claim (OperatorIdentityClaimsTransformation added
        // nothing, by definition of reaching this endpoint at all).
        var externalSubjectId = httpContext.User.FindFirstValue(JwtRegisteredClaimNames.Sub);
        if (string.IsNullOrEmpty(externalSubjectId))
        {
            return Results.Problem(statusCode: StatusCodes.Status500InternalServerError, title: "Token carries no subject claim.");
        }

        // `23-02`: captured at redemption - decisions.md §1. Same token, same claims, read the same
        // way `OperatorsEndpoints.HandleGetMyPermissionsAsync` reads them for the sign-in refresh.
        var name = httpContext.User.FindFirstValue(JwtRegisteredClaimNames.Name);
        var email = httpContext.User.FindFirstValue(JwtRegisteredClaimNames.Email);

        var result = await handler.HandleAsync(
            new RedeemOperatorInvite(externalSubjectId, request.Code, name, email), cancellationToken);

        if (result.IsFailure)
        {
            return result.Error!.Value.ToProblem(httpContext);
        }

        return Results.Ok(new RedeemOperatorInviteResponse(result.Value.OperatorId.Value, result.Value.SiteId.Value));
    }

    public sealed record CreateOperatorInviteRequest(string RoleName);

    /// <summary><see cref="Code"/> is the plaintext value, present in this response only - see
    /// `CreatedOperatorInvite`'s own remarks.</summary>
    public sealed record CreateOperatorInviteResponse(Guid OperatorInviteId, string Code, DateTimeOffset ExpiresAt);

    public sealed record RedeemOperatorInviteRequest(string Code);

    public sealed record RedeemOperatorInviteResponse(Guid OperatorId, Guid SiteId);
}
