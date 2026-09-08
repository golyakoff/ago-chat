using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Ago.Chat.Api.Auth;
using Ago.Chat.Api.Http;
using Ago.Chat.Application.UseCases.CreateOperatorInvite;
using Ago.Chat.Application.UseCases.PreviewOperatorInvite;
using Ago.Chat.Application.UseCases.RedeemOperatorInvite;
using Ago.Chat.Domain;
using Ago.Platform.Abstractions;
using Microsoft.Extensions.Options;

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
/// <item><c>POST /api/v1/operator-invites/preview</c> - `23-70`: `AllowAnonymous()`, the third gate
/// this file uses, for a caller who does not even hold a Keycloak session yet - "a stranger opening a
/// link they were sent" (this item's own backlog text). `POST` with the code in the body, deliberately
/// not `GET /operator-invites/{code}/preview` with the code in the path - a live check against this
/// deployment's own Jaeger found `url.path` recorded verbatim by the ASP.NET Core OpenTelemetry
/// instrumentation `Ago.Platform.Observability` wires up (`url.query` values are redacted by default;
/// path segments are not, and that is a library default this deployment does not control the future of).
/// `/operator-invites/redeem` right above already carries this same code in a `POST` body for the
/// identical reason - one convention for one credential, not two. Still rate-limited by IP the identical
/// way `DocumentEndpoints`' own anonymous reads are (see `OperatorInvitePreviewRateLimitOptions`'s own
/// remarks) - a `POST` that reads rather than writes is the deliberate deviation from REST here, and the
/// reason is that a credential does not belong in a URL, full stop.</item>
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

        app.MapPost("/api/v1/operator-invites/preview", HandlePreviewAsync)
            .AllowAnonymous();
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

    private static async Task<IResult> HandlePreviewAsync(
        PreviewOperatorInviteRequest request,
        PreviewOperatorInviteHandler handler,
        IRateLimiter rateLimiter,
        IOptions<OperatorInvitePreviewRateLimitOptions> rateLimitOptions,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        // Same per-IP bucket shape as `DocumentEndpoints`' own anonymous reads - "unknown" still
        // buckets a caller this route cannot otherwise identify rather than letting it past the
        // limiter entirely (that file's own remarks).
        var requestIp = httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        var options = rateLimitOptions.Value;
        var limit = await rateLimiter.CheckAsync(
            new RateLimitKey($"operator-invite-preview:ip:{requestIp}"),
            new RateLimitRule(options.PerIpCapacity, options.PerIpRefillPerSecond),
            cancellationToken);
        if (!limit.Allowed)
        {
            httpContext.Response.Headers.RetryAfter = ((int)Math.Ceiling(limit.RetryAfter.TotalSeconds)).ToString();
            return Results.Problem(
                title: "Too many requests", statusCode: StatusCodes.Status429TooManyRequests, type: "rate-limited");
        }

        var result = await handler.HandleAsync(new PreviewOperatorInvite(request.Code), cancellationToken);
        if (result.IsFailure)
        {
            // `OperatorInvite.NotFound` -> 404 (`ErrorExtensions`'s own existing mapping, unchanged by
            // this item) - the console's own landing page renders that as a plain "this link is not
            // valid" message rather than an unhandled failure, never a browser-level 404 (this item's
            // own trap: "not 404 and not throw").
            return result.Error!.Value.ToProblem(httpContext);
        }

        var dto = result.Value;
        return Results.Ok(new OperatorInvitePreviewResponse(dto.SiteName, dto.InvitedByDisplayName, dto.ExpiresAt, dto.Status.ToString()));
    }

    public sealed record CreateOperatorInviteRequest(string RoleName);

    /// <summary><see cref="Code"/> is the plaintext value, present in this response only - see
    /// `CreatedOperatorInvite`'s own remarks.</summary>
    public sealed record CreateOperatorInviteResponse(Guid OperatorInviteId, string Code, DateTimeOffset ExpiresAt);

    public sealed record RedeemOperatorInviteRequest(string Code);

    public sealed record RedeemOperatorInviteResponse(Guid OperatorId, Guid SiteId);

    /// <summary>The identical shape <see cref="RedeemOperatorInviteRequest"/> already uses for the same
    /// value - `POST` with the code in the body, never a route segment, because the code is a
    /// credential and a credential does not go in a URL (this method's own class-level remarks have the
    /// live-Jaeger evidence for why that is not merely a style preference here).</summary>
    public sealed record PreviewOperatorInviteRequest(string Code);

    /// <summary>`Status` is one of `Valid`/`Expired`/`Redeemed` (`OperatorInvitePreviewStatus`'s own
    /// three cases), sent as its enum member name - `api-design.md`: "clients branch on `type`, never
    /// on the message", the identical reasoning that governs every `ApiProblemError#code` branch this
    /// console already writes, applied here to a success response's own status field instead.</summary>
    public sealed record OperatorInvitePreviewResponse(string SiteName, string? InvitedByDisplayName, DateTimeOffset ExpiresAt, string Status);
}
