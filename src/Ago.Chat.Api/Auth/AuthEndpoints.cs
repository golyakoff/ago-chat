using Ago.Chat.Application.Abstractions;
using Ago.Chat.Application.UseCases.GetSiteByPublicKey;
using Ago.Chat.Domain;
using Ago.Platform.Abstractions;
using Ago.Platform.Kernel;
using Microsoft.AspNetCore.Authorization;
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

        // `17-08`/`adr/0048`: renewal, and deliberately a *second* endpoint rather than a flag on the
        // one above. A flag would make one route both public-unauthenticated and authenticated
        // depending on a body field, with a different rate-limit key and a different success status
        // on each path - two endpoints wearing one route.
        //
        // The Visitor scheme is the whole authentication story here: the caller proves the identity
        // it is asking to have renewed by presenting the token being renewed, so `sub` and `site_id`
        // come from the validated principal and never from the body. No policy beyond the scheme -
        // this scheme issues exactly one kind of token, so `AuthorizationPolicies.EitherTokenKind`'s
        // `kind` requirement (which exists for the *shared* attachment route) would add nothing
        // here. Same shape as `VisitorHub`'s own `[Authorize(AuthenticationSchemes = ...)]`.
        app.MapPost("/api/v1/visitor-sessions/renew", HandleVisitorSessionRenewalAsync)
            .RequireAuthorization(new AuthorizeAttribute { AuthenticationSchemes = JwtSchemes.Visitor });

        // `POST /dev/operator-session` (the Development-only stub - no password, no check that the
        // operator id was real) is removed outright in `5-05`, not left behind a flag - `adr/0022`
        // replaces it with real Keycloak-issued tokens, `Ago.Chat.Api`'s Operator scheme now
        // validates directly against Keycloak's JWKS (`Program.cs`).
    }

    public static async Task<IResult> HandleVisitorSessionAsync(
        VisitorSessionRequest request,
        GetSiteConfigByPublicKeyHandler getSite,
        ISiteInstallationSignalRepository installationSignals,
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
            // `23-06`: the half of §3's amendment an implementation drops most easily. The common
            // failure is not a tenant spreading the script over extra sites - it is `www.` against the
            // bare domain, `http` against `https`, or a staging subdomain, where *every* request is
            // refused and RecordSightingAsync below never once runs. Without this, the install screen
            // would tell somebody whose script is installed and running that it has never seen their
            // site - decisions.md §3's own "the wrong one is the discouraging one."
            await installationSignals.RecordRefusedOriginAsync(new SiteId(site.SiteId), origin, clock.UtcNow, cancellationToken);
            return Results.Problem(
                title: "Origin not allowed for this site", statusCode: StatusCodes.Status403Forbidden, type: "origin-not-allowed");
        }

        // `23-06`: the one fact this item exists to start recording - "the widget was seen." Recorded
        // only once every check above (rate limit, site lookup, origin) has passed, so a rejected
        // request never counts as a sighting; throttled to at most one row write per site per minute
        // inside RecordSightingAsync itself, not here.
        await installationSignals.RecordSightingAsync(new SiteId(site.SiteId), clock.UtcNow, cancellationToken);

        var visitorId = new VisitorId(idGenerator.NewId(clock.UtcNow));
        var token = tokens.IssueVisitorToken(visitorId, new SiteId(site.SiteId));
        return Results.Created(
            $"/api/v1/visitor-sessions/{visitorId.Value}",
            new VisitorSessionResponse(
                token, visitorId.Value, site.WidgetPrimaryColorHex, site.WidgetPosition.ToString(),
                site.WidgetLocale.ToString(), site.WidgetNoticeText, site.WidgetNoticeUrl));
    }

    /// <summary>
    /// `17-08`/`adr/0048`: a fresh token for the **same** <c>VisitorId</c>. Re-minting through the
    /// public endpoint above already "works" and is exactly what loses the visitor's history, which
    /// is why preserving the identity needs its own endpoint rather than a longer lifetime.
    ///
    /// `200 OK`, not `201`: nothing is created, one identity continues. The response is the mint's
    /// own <see cref="VisitorSessionResponse"/> - deliberately, and it closes a limitation
    /// `ago-widget/src/storage.ts` had written down as unfixable since `11-03` ("needs a session
    /// endpoint that can return current config without minting a new visitor"). A returning
    /// visitor's cached widget colour/position is now at most one renewal window stale rather than
    /// frozen at the moment their identity was first minted.
    ///
    /// `23-06`'s own stated limitation, not fixed here: `ago-widget/src/session.ts`'s `start()` only
    /// calls this endpoint when `isInRenewalWindow` says the stored token is close enough to expiry to
    /// need it - a returning visitor whose token needs no renewal makes no call at all, on either
    /// endpoint, so <c>last_seen_at</c> under-reports exactly that visitor. Tolerable for a
    /// once-a-minute freshness signal (this item's own Out of scope), and closed properly by `23-07`'s
    /// beacon, which fires on every widget mount regardless of token freshness.
    ///
    /// A named method, matching <see cref="HandleVisitorSessionAsync"/>'s shape rather than an inline
    /// lambda. Its own tests (<c>VisitorSessionRenewalTests</c>) go through a real request pipeline,
    /// not around it - two of the five cases that matter (an expired token, an anonymous caller) are
    /// decided by the scheme and the route's <c>RequireAuthorization</c> before this method is
    /// reached at all, so the direct-invocation seam the mint's tests use could not observe them.
    /// </summary>
    public static async Task<IResult> HandleVisitorSessionRenewalAsync(
        VisitorSessionRequest request,
        GetSiteConfigByPublicKeyHandler getSite,
        ISiteInstallationSignalRepository installationSignals,
        IRateLimiter rateLimiter,
        IOptions<VisitorSessionRenewalRateLimitOptions> rateLimitOptions,
        IClock clock,
        JwtTokenService tokens,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        // Both come from the *validated* principal. Taking either from the body would make this
        // endpoint a way to mint a token for any visitor of any site while holding one valid token
        // - which is the mint endpoint, except with someone else's identity attached.
        var visitorId = httpContext.User.GetVisitorId();
        var tokenSiteId = httpContext.User.GetSiteId();

        // `adr/0048`: per *visitor*, not per site as the mint is - the deliberate deviation from the
        // mint's shape, and the reason is that renewal has a visitor identity to key on while the
        // mint does not. Per-visitor stops one abusive token-holder exhausting a bucket shared with
        // every honest visitor on the same site. Checked first, before the site lookup, so a caller
        // hammering this with a junk public key still pays for it - the same ordering, and the same
        // reason, as the mint.
        var options = rateLimitOptions.Value;
        var limit = await rateLimiter.CheckAsync(
            new RateLimitKey($"visitor-session-renew:visitor:{visitorId.Value}"),
            new RateLimitRule(options.PerVisitorCapacity, options.PerVisitorRefillPerSecond),
            cancellationToken);
        if (!limit.Allowed)
        {
            httpContext.Response.Headers.RetryAfter = ((int)Math.Ceiling(limit.RetryAfter.TotalSeconds)).ToString();
            return Results.Problem(
                title: "Too many requests", statusCode: StatusCodes.Status429TooManyRequests, type: "rate-limited");
        }

        // The same cached lookup the mint uses (`3-04`), rather than a second query by `SiteId`:
        // this is the read that is already hot on every widget bootstrap, and it is what gives the
        // response its widget-config fields.
        var site = await getSite.HandleAsync(new GetSiteConfigByPublicKey(request.PublicKey), cancellationToken);
        if (site is null)
        {
            // Deliberately the mint's own answer for an unknown key, and deliberately *not* a 403.
            // An embed whose `siteKey` resolves to nothing is a misconfigured page, not a caller
            // presenting a token that has stopped being valid - and `ago-widget` (`17-07`) reads
            // `401`/`403` as "this identity is finished" and everything else as transient. A `403`
            // here would end the sessions of every visitor on a site whose public key was rotated,
            // rather than leaving them on the valid token they still hold.
            return Results.Problem(
                title: "Site not found", statusCode: StatusCodes.Status404NotFound, type: "site-not-found");
        }

        // The check this endpoint exists to make. The token says which site it belongs to; the body
        // says which site the caller claims to be embedded on. A token minted for site A must not be
        // renewable by presenting site B's public key - otherwise the response would hand back a
        // token still claiming site A while the caller's own site config decided the origin rules
        // that let the request through.
        //
        // Before the origin check on purpose: a caller probing with a public key that is not theirs
        // learns only "not your site", never anything about that site's allowed origins.
        if (site.SiteId != tokenSiteId.Value)
        {
            return Results.Problem(
                title: "This session belongs to a different site",
                statusCode: StatusCodes.Status403Forbidden,
                type: "site-mismatch");
        }

        // `5-01`, layer 2 - identical to the mint's, and for the identical reason: CORS (layer 1)
        // only proved this Origin belongs to *some* site. A missing Origin header has no
        // cross-origin claim to verify and is left alone.
        var origin = httpContext.Request.Headers.Origin.ToString();
        if (!string.IsNullOrEmpty(origin) && !site.AllowedOrigins.Contains(origin))
        {
            // `23-06`: the identical refusal recording the mint endpoint above does, and for the
            // identical reason - a returning visitor whose origin now mismatches (a domain rename, a
            // staging deploy) must not silently stop updating last_seen_at with no trace of why.
            await installationSignals.RecordRefusedOriginAsync(tokenSiteId, origin, clock.UtcNow, cancellationToken);
            return Results.Problem(
                title: "Origin not allowed for this site", statusCode: StatusCodes.Status403Forbidden, type: "origin-not-allowed");
        }

        // `23-06`: a returning visitor renews rather than mints (this item's own Scope), so this is the
        // sighting write for that path - the mint endpoint's own comment on why this comes only after
        // every check above has passed applies here verbatim.
        await installationSignals.RecordSightingAsync(tokenSiteId, clock.UtcNow, cancellationToken);

        var token = tokens.IssueVisitorToken(visitorId, tokenSiteId);
        return Results.Ok(new VisitorSessionResponse(
            token, visitorId.Value, site.WidgetPrimaryColorHex, site.WidgetPosition.ToString(),
            site.WidgetLocale.ToString(), site.WidgetNoticeText, site.WidgetNoticeUrl));
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
    ///
    /// `11-10`: <see cref="WidgetLocale"/> joins on the identical terms - a flat, additive sibling
    /// field, not nested under the two above, crossing the wire as `Locale`'s own PascalCase member
    /// name (`"En"`/`"Ru"`). `ago-widget`'s `ui/i18n/resolve.ts` is where an unrecognised or missing
    /// value falls back to `"en"` silently, the same "courtesy re-check, never trust the wire value
    /// blindly" posture `ui/appearance.ts`'s `parseWidgetPosition` already takes for the sibling field
    /// beside it.
    ///
    /// `16-04`: <see cref="WidgetNoticeText"/>/<see cref="WidgetNoticeUrl"/> join as two more additive,
    /// nullable fields - the tenant's own sentence about who processes what a visitor is about to
    /// write, and where to read more, both `null` (rendering nothing) for every site that has not
    /// configured one. `ago-widget`'s `ui/appearance.ts` re-validates both on receipt the same
    /// "courtesy re-check, never trust the wire value blindly" way it already does for color and
    /// position - a malformed or non-`https://` URL here (a wire value never trusted blindly, `WidgetConfig`'s
    /// own server-side validation notwithstanding) falls back to rendering no link, never a thrown
    /// exception on the host page.
    /// </summary>
    public sealed record VisitorSessionResponse(
        string Token,
        Guid VisitorId,
        string? WidgetPrimaryColorHex,
        string WidgetPosition,
        string WidgetLocale,
        string? WidgetNoticeText,
        string? WidgetNoticeUrl);
}
