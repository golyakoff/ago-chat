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

        // Dev-only: trades an operator id for a session token directly, no password, no check that
        // the id is real - IPermissionChecker (adr/0016) is what actually gates anything this token
        // is used for. Mapped only in Development, never reachable otherwise, and replaced outright
        // by OIDC at Stage 5, not evolved into it (authorization.md).
        if (app.Environment.IsDevelopment())
        {
            app.MapPost("/dev/operator-session", (OperatorSessionRequest request, JwtTokenService tokens) =>
            {
                var token = tokens.IssueOperatorToken(new OperatorId(request.OperatorId), new SiteId(request.SiteId));
                return Results.Ok(new OperatorSessionResponse(token));
            });
        }
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

        var visitorId = new VisitorId(idGenerator.NewId(clock.UtcNow));
        var token = tokens.IssueVisitorToken(visitorId, new SiteId(site.SiteId));
        return Results.Created(
            $"/api/v1/visitor-sessions/{visitorId.Value}",
            new VisitorSessionResponse(token, visitorId.Value));
    }

    public sealed record VisitorSessionRequest(string PublicKey);

    public sealed record VisitorSessionResponse(string Token, Guid VisitorId);

    public sealed record OperatorSessionRequest(Guid OperatorId, Guid SiteId);

    public sealed record OperatorSessionResponse(string Token);
}
