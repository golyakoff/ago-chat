using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Ago.Chat.Api.Auth;
using Ago.Chat.Api.Http;
using Ago.Chat.Application.Abstractions;
using Ago.Chat.Application.UseCases.GetAccessRecordsForSite;
using Ago.Chat.Application.UseCases.GetMessageArchiveDownloadUrl;
using Ago.Chat.Application.UseCases.GetSiteExportStatus;
using Ago.Chat.Application.UseCases.ListMessageArchives;
using Ago.Chat.Application.UseCases.RegisterSite;
using Ago.Chat.Application.UseCases.RequestSiteErasure;
using Ago.Chat.Application.UseCases.RequestSiteExport;
using Ago.Chat.Contracts;
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

        // `16-03`: same siteId-from-the-route convention as `/erase` right above, and the same
        // reasoning - PermissionChecker.HasPermissionAsync checks this specific (OperatorId, SiteId)
        // pair regardless of which site the caller's token happens to be scoped to right now.
        app.MapPost("/api/v1/sites/{siteId:guid}/exports", HandleRequestExportAsync)
            .RequireAuthorization("RequireOperatorIdentity");

        app.MapGet("/api/v1/sites/{siteId:guid}/exports/{exportId:guid}", HandleGetExportStatusAsync)
            .RequireAuthorization("RequireOperatorIdentity");

        // `13-06`: same siteId-from-the-route convention, same reasoning, as `/exports` above - a
        // tenant's own archived retention periods, list then download-by-key. No POST/request route:
        // unlike `/exports`, the archive already exists by the time an operator could ask for one
        // (`ListMessageArchivesHandler`'s own remarks) - there is nothing to trigger.
        app.MapGet("/api/v1/sites/{siteId:guid}/message-archives", HandleListMessageArchivesAsync)
            .RequireAuthorization("RequireOperatorIdentity");

        app.MapGet(
                "/api/v1/sites/{siteId:guid}/message-archives/{retentionClass}/{period}/download",
                HandleGetMessageArchiveDownloadUrlAsync)
            .RequireAuthorization("RequireOperatorIdentity");
    }

    /// <summary>
    /// `24-12`: `GET /api/v1/sites/{siteId}/access-records` - deliberately its own <c>Map</c> call,
    /// not folded into <see cref="MapSitesEndpoints"/> above. The same "own file, own Map call" seam
    /// <c>OwnerSitesEndpoints</c>'s own class remarks describe for <c>MapOwnerSiteDetailEndpoint</c>,
    /// for the identical reason found running this item: several integration tests build a
    /// stripped-down <see cref="WebApplication"/> that calls <see cref="MapSitesEndpoints"/> to
    /// exercise routes that predate this one, without ever registering <see cref="GetAccessRecordsForSiteHandler"/>
    /// in their own DI container. ASP.NET Core's Minimal API cannot build *any* endpoint's metadata
    /// once one endpoint's service parameter cannot be recognised as a service - so folding this route
    /// into <see cref="MapSitesEndpoints"/> broke every other route in that method, in every test host
    /// that never touches this one at all (35 failing tests across
    /// <c>SiteRegistrationTests</c>/<c>OperatorInviteEndpointTests</c>/<c>ActiveSiteResolutionTests</c>/
    /// <c>PlatformOwnerAsTenantTests</c>/<c>OwnerSiteDetailEndpointTests</c>/<c>OwnerModuleEndpointsTests</c>,
    /// all with the identical "Body was inferred but the method does not allow inferred body
    /// parameters" exception, found by running the full suite rather than this file's own tests in
    /// isolation). Two map calls is what keeps a test host's registrations matching exactly the routes
    /// it maps.
    /// </summary>
    public static void MapAccessRecordsEndpoint(this WebApplication app)
    {
        app.MapGet("/api/v1/sites/{siteId:guid}/access-records", HandleGetAccessRecordsAsync)
            .RequireAuthorization("RequireOperatorIdentity");
    }

    // `ago-root#353`: public, not private - `AttachmentEndpoints.HandleCreateAsync`'s own reasoning,
    // itself following `AuthEndpoints`/`DemoEndpoints`'s precedent: a test can call this directly to
    // prove the Retry-After header, no hosting pipeline needed.
    public static async Task<IResult> HandleRegisterSiteAsync(
        RegisterSiteRequest request,
        RegisterSiteHandler handler,
        RegisterSiteRateLimitOptions rateLimitOptions,
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

        // `23-02`: this endpoint also creates an `Operator` from a real human's token
        // (`RegisterSiteHandler`'s own remarks) - "pass whatever it has", same as the invite-redemption
        // path.
        var name = httpContext.User.FindFirstValue(JwtRegisteredClaimNames.Name);
        var email = httpContext.User.FindFirstValue(JwtRegisteredClaimNames.Email);

        // `24-03`: the request's own `User-Agent` header, becoming AcceptanceRecord's own request
        // context field if this registration ends up recording one - `null` rather than an empty
        // string when the header is absent, the same "do not invent a value" reasoning `requestIp`'s
        // own fallback above stops short of (that one still buckets an unknown IP together for rate
        // limiting; there is no equivalent reason to invent a user agent for evidence). Truncated to
        // AcceptanceRecord.MaxUserAgentLength here, at the edge - a header longer than that bound is
        // presentation noise (an unusually verbose real browser string, or a crafted one), not a fact
        // worth failing a registration over; AcceptanceRecord.Accept would otherwise throw for a value
        // this endpoint controls, not the caller's own business input.
        var rawUserAgent = httpContext.Request.Headers.UserAgent.ToString();
        var userAgent = rawUserAgent.Length == 0
            ? null
            : rawUserAgent.Length > AcceptanceRecord.MaxUserAgentLength
                ? rawUserAgent[..AcceptanceRecord.MaxUserAgentLength]
                : rawUserAgent;

        var result = await handler.HandleAsync(
            new RegisterSite(externalSubjectId, requestIp, request.SiteName, request.InitialAllowedOrigin, name, email, userAgent),
            cancellationToken);

        if (result.IsFailure)
        {
            var error = result.Error!.Value;
            // `ago-root#353`: the subject and IP buckets `RegisterSiteHandler` checks share this one
            // code - the slower of the two is the safe conservative answer either way.
            var retryAfter = error.Code == "Site.RateLimited"
                ? RateLimitRetryAfter.Conservative(rateLimitOptions.PerSubjectRefillPerSecond, rateLimitOptions.PerIpRefillPerSecond)
                : (TimeSpan?)null;
            return error.ToProblem(httpContext, retryAfter);
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

    /// <summary>
    /// `16-03`: `POST /api/v1/sites/{siteId}/exports` - inserts one <c>Pending</c> export request and
    /// returns immediately; no packaging happens on this request (`RequestSiteExportHandler`'s own
    /// remarks). `202 Accepted`, the same code `/erase` returns and for the identical reason - the
    /// request is accepted, `Ago.Chat.Worker`'s `SiteExportJob` has not run yet, and "is the archive
    /// ready" is honestly "not yet." Unlike `/erase`, the response body carries an id
    /// (<see cref="RequestSiteExportResponse"/>) - erasure ends in "gone" and needs nothing further
    /// from the caller, while export produces an artifact the caller must be able to poll for, so the
    /// `Location` header alone (pointing at the status endpoint below) would leave a caller that does
    /// not parse response headers with no way to find its own request again.
    /// </summary>
    // `ago-root#353`: public, not private - same reasoning as `HandleRegisterSiteAsync` above.
    public static async Task<IResult> HandleRequestExportAsync(
        Guid siteId, RequestSiteExportHandler handler, SiteExportRateLimitOptions rateLimitOptions,
        HttpContext httpContext, CancellationToken cancellationToken)
    {
        var user = httpContext.User;
        var result = await handler.HandleAsync(
            new RequestSiteExport(new SiteId(siteId), user.GetOperatorId()), cancellationToken);

        if (result.IsFailure)
        {
            var error = result.Error!.Value;
            // `ago-root#353`: one bucket, so this is the exact wait, not a max over several - still
            // computed from configuration, never a second IRateLimiter.CheckAsync.
            var retryAfter = error.Code == "Export.RateLimited"
                ? RateLimitRetryAfter.Conservative(rateLimitOptions.PerSiteRefillPerSecond)
                : (TimeSpan?)null;
            return error.ToProblem(httpContext, retryAfter);
        }

        var exportId = result.Value;
        return Results.Accepted(
            $"/api/v1/sites/{siteId}/exports/{exportId}", new RequestSiteExportResponse(exportId));
    }

    /// <summary>
    /// `16-03`: `GET /api/v1/sites/{siteId}/exports/{exportId}` - the completion poll
    /// `usePollUntilErased`'s own console-side sibling is expected to drive, the same shape `16-02`'s
    /// `GetConversationByIdHandler`/`ConversationsEndpoints` route already established for erasure.
    /// </summary>
    private static async Task<IResult> HandleGetExportStatusAsync(
        Guid siteId, Guid exportId, GetSiteExportStatusHandler handler, HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        var user = httpContext.User;
        var result = await handler.HandleAsync(
            new GetSiteExportStatus(exportId, new SiteId(siteId), user.GetOperatorId()), cancellationToken);

        if (result.IsFailure)
        {
            return result.Error!.Value.ToProblem(httpContext);
        }

        var item = result.Value;
        return Results.Ok(new SiteExportStatusResponse(
            item.ExportId, item.Status.ToString(), item.RequestedAt, item.CompletedAt, item.DownloadUrl, item.FailureReason));
    }

    /// <summary>
    /// `24-12`: `GET /api/v1/sites/{siteId}/access-records` - the tenant's own read of who accessed
    /// their data, per this item's own Scope ("reachable by the tenant for their own site, not only by
    /// AGO"). `?before=&limit=` matches `api-design.md`'s pagination convention, the same spelling
    /// `OwnerSitesEndpoints`'s own cross-tenant list uses.
    /// </summary>
    private static async Task<IResult> HandleGetAccessRecordsAsync(
        Guid siteId, Guid? before, int? limit, GetAccessRecordsForSiteHandler handler, HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        var user = httpContext.User;
        var result = await handler.HandleAsync(
            new GetAccessRecordsForSite(new SiteId(siteId), user.GetOperatorId(), before, limit), cancellationToken);

        if (result.IsFailure)
        {
            return result.Error!.Value.ToProblem(httpContext);
        }

        var page = result.Value;
        return Results.Ok(new AccessRecordsResponse(page.Items.Select(ToDto).ToList(), page.NextBeforeId));
    }

    private static AccessRecordDto ToDto(AccessRecordItem item) => new(
        item.Id, item.OccurredAt, item.AccessKind.ToString(), item.ActorKind.ToString(), item.ActorId,
        item.ResourceKind?.ToString(), item.ResourceId);

    /// <summary>`13-06`: `GET /api/v1/sites/{siteId}/message-archives` - every retention period this
    /// site currently has an archive object for, newest first.</summary>
    private static async Task<IResult> HandleListMessageArchivesAsync(
        Guid siteId, ListMessageArchivesHandler handler, HttpContext httpContext, CancellationToken cancellationToken)
    {
        var user = httpContext.User;
        var result = await handler.HandleAsync(
            new ListMessageArchives(new SiteId(siteId), user.GetOperatorId()), cancellationToken);

        if (result.IsFailure)
        {
            return result.Error!.Value.ToProblem(httpContext);
        }

        return Results.Ok(result.Value
            .Select(r => new MessageArchiveResponse(r.RetentionClass.Value, r.PeriodStart, r.PeriodEnd, r.ArchivedAt))
            .ToList());
    }

    /// <summary>`13-06`: `GET /api/v1/sites/{siteId}/message-archives/{retentionClass}/{period}/download` -
    /// <paramref name="period"/> is `yyyy-MM` (the console's own natural rendering of a monthly
    /// partition, and this route's one caller-facing shorthand for `DateOnly`'s otherwise-full-date
    /// route-binding). A malformed period is a `400`, not a `404` - the request itself is
    /// unparseable, which is a different fact from "no archive matches a period this request did
    /// successfully parse."</summary>
    private static async Task<IResult> HandleGetMessageArchiveDownloadUrlAsync(
        Guid siteId, string retentionClass, string period, GetMessageArchiveDownloadUrlHandler handler,
        HttpContext httpContext, CancellationToken cancellationToken)
    {
        if (!DateOnly.TryParseExact($"{period}-01", "yyyy-MM-dd", out var periodStart))
        {
            return Results.Problem(
                title: "Invalid period", detail: $"'{period}' is not a valid yyyy-MM period.", statusCode: StatusCodes.Status400BadRequest);
        }

        var user = httpContext.User;
        var result = await handler.HandleAsync(
            new GetMessageArchiveDownloadUrl(new SiteId(siteId), new RetentionClass(retentionClass), periodStart, user.GetOperatorId()),
            cancellationToken);

        if (result.IsFailure)
        {
            return result.Error!.Value.ToProblem(httpContext);
        }

        return Results.Ok(new MessageArchiveDownloadResponse(result.Value));
    }

    public sealed record RegisterSiteRequest(string SiteName, string InitialAllowedOrigin);

    public sealed record RegisterSiteResponse(Guid SiteId, Guid OperatorId);

    public sealed record RequestSiteExportResponse(Guid ExportId);

    /// <summary>
    /// <paramref name="Status"/> is one of <c>"Pending"</c>, <c>"Ready"</c>, <c>"Failed"</c> -
    /// <see cref="Domain.ExportStatus"/>'s own member names, serialised via <c>ToString()</c> rather
    /// than System.Text.Json's numeric default, so a client reads a name, not an enum ordinal it would
    /// have to keep in sync with this codebase's own declaration order.
    /// </summary>
    public sealed record SiteExportStatusResponse(
        Guid ExportId, string Status, DateTimeOffset RequestedAt, DateTimeOffset? CompletedAt, Uri? DownloadUrl, string? FailureReason);

    public sealed record MessageArchiveResponse(string RetentionClass, DateOnly PeriodStart, DateOnly PeriodEnd, DateTimeOffset ArchivedAt);

    public sealed record MessageArchiveDownloadResponse(Uri DownloadUrl);
}
