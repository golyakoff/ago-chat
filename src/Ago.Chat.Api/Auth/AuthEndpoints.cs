using Ago.Chat.Application.UseCases.GetSiteByPublicKey;
using Ago.Chat.Domain;
using Ago.Platform.Abstractions;
using Ago.Platform.Kernel;
using Microsoft.Extensions.Options;

namespace Ago.Chat.Api.Auth;

public static class AuthEndpoints
{
    public static void MapAuthEndpoints(this WebApplication app)
    {
        // The real mechanism: a visitor's own token-issuance path, not a stub. Anyone who knows a
        // site's public key (not a secret - api-design.md) can start a session; nothing sensitive is
        // granted until the visitor sends a message, at which point Conversation.AddVisitorMessage
        // (1-01) checks it is this same visitor.
        //
        // 3-04: this is caching.md's "the hot one" - every widget bootstrap hits it, so the site
        // lookup goes through GetSiteConfigByPublicKeyHandler's cache-aside read rather than ISiteRepository
        // directly, the way it did before this slice.
        //
        // A named method, not an inline lambda: this is the one endpoint 3-05 needs to prove is
        // actually wired to the rate limiter, not just that IRateLimiter exists somewhere unused
        // (its own Done-when). Minimal API happily takes a method group, and the same method is then
        // directly callable from a test with hand-built dependencies - no hosting/routing pipeline
        // needed, the same "construct it directly, no full server" seam 3-03 used for VisitorHub.
        app.MapPost("/api/v1/visitor-sessions", HandleVisitorSessionAsync);

        // `POST /dev/operator-session` (the Development-only stub - no password, no check that the
        // operator id was real) is removed outright in `5-05`, not left behind a flag - `adr/0022`
        // replaces it with real Keycloak-issued tokens, `Ago.Chat.Api`'s Operator scheme now
        // validates directly against Keycloak's JWKS (`Program.cs`).
    }

    public static async Task<IResult> HandleVisitorSessionAsync(
        VisitorSessionRequest request,
        GetSiteConfigByPublicKeyHandler getSite,
        IRateLimiter rateLimiter,
        IOptions<VisitorSessionRateLimitOptions> rateLimitOptions,
        IIdGenerator idGenerator,
        IClock clock,
        JwtTokenService tokens,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        // 3-05: per-site only, keyed by the public key itself - there is no visitor id yet, that is
        // what this endpoint is about to mint. Checked before the site lookup: a bad or unknown
        // public key still costs the caller a token, exactly the abuse case this guards.
        var options = rateLimitOptions.Value;
        var limit = await rateLimiter.CheckAsync(
            new RateLimitKey($"visitor-session:site:{request.PublicKey}"),
            new RateLimitRule(options.PerSiteCapacity, options.PerSiteRefillPerSecond),
            cancellationToken);
        if (!limit.Allowed)
        {
            httpContext.Response.Headers.RetryAfter = ((int)Math.Ceiling(limit.RetryAfter.TotalSeconds)).ToString();
            return Results.Problem(
                title: "Too many requests", statusCode: StatusCodes.Status429TooManyRequests, type: "rate-limited");
        }

        var site = await getSite.HandleAsync(new GetSiteConfigByPublicKey(request.PublicKey), cancellationToken);
        if (site is null)
        {
            return Results.Problem(
                title: "Site not found", statusCode: StatusCodes.Status404NotFound, type: "site-not-found");
        }

        // 5-01, layer 2: the real per-site boundary - SiteOriginCorsPolicyProvider (layer 1) only
        // proved this Origin belongs to *some* site's AllowedOrigins, not *this* one. A missing
        // Origin header (a same-origin caller - the dev harness, local-dev.md) has no cross-origin
        // claim to verify and is left alone; a present Origin not in this specific site's list is
        // rejected even though CORS already let the request through.
        var origin = httpContext.Request.Headers.Origin.ToString();
        if (!string.IsNullOrEmpty(origin) && !site.AllowedOrigins.Contains(origin))
        {
            return Results.Problem(
                title: "Origin not allowed for this site", statusCode: StatusCodes.Status403Forbidden, type: "origin-not-allowed");
        }

        var visitorId = new VisitorId(idGenerator.NewId(clock.UtcNow));
        var token = tokens.IssueVisitorToken(visitorId, new SiteId(site.SiteId));
        return Results.Created(
            $"/api/v1/visitor-sessions/{visitorId.Value}",
            new VisitorSessionResponse(
                token, visitorId.Value, site.WidgetPrimaryColorHex, site.WidgetPosition.ToString()));
    }

    public sealed record VisitorSessionRequest(string PublicKey);

    /// <summary>
    /// `11-01`: <see cref="WidgetPrimaryColorHex"/>/<see cref="WidgetPosition"/> are additive fields,
    /// never a second round trip - `SiteConfigDto` (the cached DTO `getSite` above already returns)
    /// carries them since `11-01`'s Application-layer commit, so this handshake response is the one
    /// piece that needed to actually surface them onto the wire. `embeddable-widget`'s own Bootstrap
    /// section states the intended shape this finally makes true: "the handshake returns the site's
    /// widget settings ... and the visitor's history cursor." `WidgetPosition` crosses the wire as its
    /// PascalCase member name (`"BottomRight"`/`"BottomLeft"`), matching `WidgetConfigEndpoints`'s own
    /// convention for the same value.
    /// </summary>
    public sealed record VisitorSessionResponse(
        string Token, Guid VisitorId, string? WidgetPrimaryColorHex, string WidgetPosition);
}
