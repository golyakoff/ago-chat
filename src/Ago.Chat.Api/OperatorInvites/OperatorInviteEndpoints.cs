using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Ago.Chat.Api.Auth;
using Ago.Chat.Api.Http;
using Ago.Chat.Application.UseCases;
using Ago.Chat.Application.UseCases.CreateOperatorInvite;
using Ago.Chat.Application.UseCases.HasPendingOperatorInvite;
using Ago.Chat.Application.UseCases.ListOperatorInvites;
using Ago.Chat.Application.UseCases.PreviewOperatorInvite;
using Ago.Chat.Application.UseCases.RedeemOperatorInvite;
using Ago.Chat.Application.UseCases.RedeemPendingOperatorInviteForCaller;
using Ago.Chat.Application.UseCases.RevokeOperatorInvite;
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

        // `25-73`: the console's own invite-list screen and its "отозвать" button - both gated the same
        // `RequireOperatorIdentity` way as creation right above, `Permission.SiteManageOperators`
        // checked inside each handler, not at this route's own policy layer.
        app.MapGet("/api/v1/sites/{siteId:guid}/operator-invites", HandleListAsync)
            .RequireAuthorization("RequireOperatorIdentity");

        app.MapPost("/api/v1/sites/{siteId:guid}/operator-invites/{operatorInviteId:guid}/revoke", HandleRevokeAsync)
            .RequireAuthorization("RequireOperatorIdentity");

        app.MapPost("/api/v1/operator-invites/redeem", HandleRedeemAsync)
            .RequireAuthorization("RequireKeycloakIdentity");

        // `25-85`: the "activate it here" card's own no-code redemption - RequireKeycloakIdentity, the
        // identical policy the code-based redeem route above uses and for the identical reason: this
        // caller has no `operators` row yet either. See `RedeemPendingOperatorInviteForCallerHandler`'s
        // own remarks for why an authenticated caller with no code in hand can still redeem safely.
        app.MapPost("/api/v1/operator-invites/redeem-pending-for-me", HandleRedeemPendingForCallerAsync)
            .RequireAuthorization("RequireKeycloakIdentity");

        // `25-73`: OnboardingPage's own registration-collision steer - RequireKeycloakIdentity, the
        // identical policy the redeem route uses and for the identical reason: this caller may resolve
        // to no `operators` row at all.
        app.MapGet("/api/v1/operator-invites/pending-for-me", HandleHasPendingInviteAsync)
            .RequireAuthorization("RequireKeycloakIdentity");

        app.MapPost("/api/v1/operator-invites/preview", HandlePreviewAsync)
            .AllowAnonymous();
    }

    private static async Task<IResult> HandleHasPendingInviteAsync(
        HasPendingOperatorInviteHandler handler, HttpContext httpContext, CancellationToken cancellationToken)
    {
        // No email claim at all reads as "no pending invite" - there is nothing to compare against,
        // the identical "cannot agree with anything" reading RedeemOperatorInviteHandler's own remarks
        // give a missing email elsewhere on this same redemption path.
        var email = httpContext.User.FindFirstValue(JwtRegisteredClaimNames.Email);
        var hasPendingInvite = email is null
            ? false
            : await handler.HandleAsync(new HasPendingOperatorInvite(email), cancellationToken);

        return Results.Ok(new HasPendingOperatorInviteResponse(hasPendingInvite));
    }

    private static async Task<IResult> HandleCreateAsync(
        Guid siteId,
        CreateOperatorInviteRequest request,
        CreateOperatorInviteHandler handler,
        OperatorInviteCreationRateLimitOptions rateLimitOptions,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        var user = httpContext.User;
        var result = await handler.HandleAsync(
            new CreateOperatorInvite(user.GetOperatorId(), new SiteId(siteId), request.RoleName, request.Email),
            cancellationToken);

        if (result.IsFailure)
        {
            var error = result.Error!.Value;
            // `25-73`: the identical `RateLimitRetryAfter.Conservative` shape every other rate-limited
            // code in this codebase already uses - this endpoint holds the same options its own
            // handler used to make the decision, so no second `IRateLimiter.CheckAsync` is needed.
            var retryAfter = error.Code == "OperatorInvite.RateLimited"
                ? RateLimitRetryAfter.Conservative(rateLimitOptions.PerSiteRefillPerSecond)
                : (TimeSpan?)null;
            return error.ToProblem(httpContext, retryAfter);
        }

        // `201`, not `200` - a new operator_invites row was created, matching
        // RegisterWebhookEndpointHandler's own "shown exactly once" precedent for a different generated
        // bearer secret. `25-73`: Location now points at a real resource - the list endpoint this same
        // item adds, filtered client-side to the one row, rather than the "no matching GET" gap this
        // comment used to name (this item's own new read surface closes it).
        return Results.Created(
            $"/api/v1/sites/{siteId}/operator-invites",
            new CreateOperatorInviteResponse(
                result.Value.OperatorInviteId, result.Value.Code, result.Value.ExpiresAt, result.Value.SendFailed));
    }

    /// <summary>`25-73`: the console's own invite-list screen - "shown only when at least one invite
    /// exists for the site" (this item's own point 7), so this always returns `200` with a (possibly
    /// empty) array rather than a `404`/`204` the console would have to special-case.</summary>
    private static async Task<IResult> HandleListAsync(
        Guid siteId, ListOperatorInvitesHandler handler, HttpContext httpContext, CancellationToken cancellationToken)
    {
        var user = httpContext.User;
        var result = await handler.HandleAsync(new ListOperatorInvites(user.GetOperatorId(), new SiteId(siteId)), cancellationToken);

        if (result.IsFailure)
        {
            return result.Error!.Value.ToProblem(httpContext);
        }

        return Results.Ok(new ListOperatorInvitesResponse(
            [.. result.Value.Select(entry => new OperatorInviteListEntryResponse(
                entry.OperatorInviteId, entry.Email, entry.CreatedAt, entry.ExpiresAt,
                entry.Status.ToString(), entry.SmtpErrorCode))]));
    }

    /// <summary>`25-73`: revoking before acceptance means a later redemption attempt is refused with
    /// `OperatorInvite.Revoked` (`RedeemOperatorInviteHandler`'s own new switch arm) - this endpoint's
    /// own job is only to translate <see cref="RevokeOperatorInviteHandler"/>'s `Result` into a
    /// response, the same shape every other write endpoint in this file already follows.</summary>
    private static async Task<IResult> HandleRevokeAsync(
        Guid siteId, Guid operatorInviteId, RevokeOperatorInviteHandler handler, HttpContext httpContext, CancellationToken cancellationToken)
    {
        var user = httpContext.User;
        var result = await handler.HandleAsync(
            new RevokeOperatorInvite(user.GetOperatorId(), new SiteId(siteId), new OperatorInviteId(operatorInviteId)),
            cancellationToken);

        return result.IsFailure ? result.Error!.Value.ToProblem(httpContext) : Results.NoContent();
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

    /// <summary>`25-85`: the "activate it here" card's own destination - no request body at all, unlike
    /// <see cref="HandleRedeemAsync"/>, because there is no code for a caller to carry. Every value this
    /// call needs already lives on the validated token, the identical claims-reading shape
    /// <see cref="HandleRedeemAsync"/> already uses right above.</summary>
    private static async Task<IResult> HandleRedeemPendingForCallerAsync(
        RedeemPendingOperatorInviteForCallerHandler handler, HttpContext httpContext, CancellationToken cancellationToken)
    {
        var externalSubjectId = httpContext.User.FindFirstValue(JwtRegisteredClaimNames.Sub);
        if (string.IsNullOrEmpty(externalSubjectId))
        {
            return Results.Problem(statusCode: StatusCodes.Status500InternalServerError, title: "Token carries no subject claim.");
        }

        var email = httpContext.User.FindFirstValue(JwtRegisteredClaimNames.Email);
        if (string.IsNullOrEmpty(email))
        {
            // No email claim at all reads as "nothing to auto-redeem" - the identical "cannot agree
            // with anything" reading `HandleHasPendingInviteAsync` already gives a missing email above,
            // never a 500: this caller may simply hold an identity whose token carries no email at all.
            return ConversationErrors.OperatorInviteNoAutoRedeemablePendingInvite().ToProblem(httpContext);
        }

        var name = httpContext.User.FindFirstValue(JwtRegisteredClaimNames.Name);

        var result = await handler.HandleAsync(
            new RedeemPendingOperatorInviteForCaller(externalSubjectId, email, name), cancellationToken);

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

    /// <summary>`25-73`: <see cref="Email"/> is required - refused by <see cref="CreateOperatorInviteHandler"/>
    /// itself (`ConversationErrors.OperatorInviteInvalidEmail`) when absent or malformed, never only
    /// hidden by the console's own form.</summary>
    public sealed record CreateOperatorInviteRequest(string RoleName, string Email);

    /// <summary><see cref="Code"/> is the plaintext value, present in this response only - see
    /// `CreatedOperatorInvite`'s own remarks. `25-73`: <see cref="SendFailed"/> - see that record's own
    /// remarks for why a send failure does not fail this call outright.</summary>
    public sealed record CreateOperatorInviteResponse(Guid OperatorInviteId, string Code, DateTimeOffset ExpiresAt, bool SendFailed);

    /// <summary>`25-73`: the console's own invite-list screen - "Status" is one of `Sent`/`SendFailed`/
    /// `Revoked`/`Redeemed`/`Expired` (`OperatorInviteListStatus`'s own five cases), sent as its enum
    /// member name, the identical `api-design.md` "clients branch on `type`, never on the message"
    /// convention `OperatorInvitePreviewResponse.Status` already follows below.</summary>
    public sealed record ListOperatorInvitesResponse(IReadOnlyList<OperatorInviteListEntryResponse> Invites);

    public sealed record OperatorInviteListEntryResponse(
        Guid OperatorInviteId, string Email, DateTimeOffset CreatedAt, DateTimeOffset ExpiresAt, string Status, string? SmtpErrorCode);

    /// <summary>`25-73`: `OnboardingPage`'s own registration-collision steer.</summary>
    public sealed record HasPendingOperatorInviteResponse(bool HasPendingInvite);

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
