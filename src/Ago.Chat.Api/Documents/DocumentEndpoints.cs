using Ago.Chat.Api.Http;
using Ago.Chat.Application.UseCases.GetDocumentVersion;
using Ago.Platform.Abstractions;
using Microsoft.Extensions.Options;

namespace Ago.Chat.Api.Documents;

/// <summary>
/// `24-02`'s own published surface - the two routes a person with no account (nobody has accepted
/// anything yet) reads a document through. Unauthenticated on purpose: `24-02`'s own Scope states it
/// outright - "somebody who has not yet accepted anything has no account to read it from" - so any
/// gate at all defeats the point, the same reasoning <c>DemoEndpoints</c>'s own remarks give for its
/// own anonymous route.
///
/// <para><b>Two routes, not one with an optional query parameter.</b> <c>GET .../{documentKey}</c>
/// answers "what does this say right now" (the current version); <c>GET .../{documentKey}/versions/{version}</c>
/// answers "what did version 4 say" - a support conversation's own sentence, `24-02`'s own Scope. Two
/// distinct URLs rather than one with <c>?version=</c> because a specific version's response is safe to
/// cache far more aggressively than "current" ever can be (immutable vs. a moving pointer,
/// <c>GetDocumentVersionHandler</c>'s own remarks) - a difference worth surfacing at the URL level, not
/// buried in a query string a cache layer in front of this API might not vary on.</para>
///
/// <para><b>Rate-limited by IP, not by any identity this endpoint has</b> - the same shape
/// <c>DemoEndpoints.HandleMintAsync</c> already uses for its own anonymous caller: nothing else exists
/// to key a bucket on. Checked here, at the HTTP edge, rather than inside
/// <see cref="GetDocumentVersionHandler"/> - unlike <c>MintDemoTenantHandler</c>, this handler creates
/// no row and its own cache-aside read already blunts most repeat-request cost, so the limiter is a pure
/// edge-level abuse guard with no domain decision riding on it, the identical placement
/// <c>AuthEndpoints.HandleVisitorSessionAsync</c> already uses for its own per-site token-issuance
/// limiter.</para>
/// </summary>
public static class DocumentEndpoints
{
    public static void MapDocumentEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/v1/documents").AllowAnonymous();

        group.MapGet("/{documentKey}", HandleGetCurrentAsync);
        group.MapGet("/{documentKey}/versions/{version}", HandleGetVersionAsync);
    }

    private static Task<IResult> HandleGetCurrentAsync(
        string documentKey,
        GetDocumentVersionHandler handler,
        IRateLimiter rateLimiter,
        IOptions<PublishedDocumentReadRateLimitOptions> rateLimitOptions,
        HttpContext httpContext,
        CancellationToken cancellationToken) =>
        HandleAsync(new GetDocumentVersion(documentKey, null), handler, rateLimiter, rateLimitOptions, httpContext, cancellationToken);

    private static Task<IResult> HandleGetVersionAsync(
        string documentKey,
        string version,
        GetDocumentVersionHandler handler,
        IRateLimiter rateLimiter,
        IOptions<PublishedDocumentReadRateLimitOptions> rateLimitOptions,
        HttpContext httpContext,
        CancellationToken cancellationToken) =>
        HandleAsync(new GetDocumentVersion(documentKey, version), handler, rateLimiter, rateLimitOptions, httpContext, cancellationToken);

    private static async Task<IResult> HandleAsync(
        GetDocumentVersion query,
        GetDocumentVersionHandler handler,
        IRateLimiter rateLimiter,
        IOptions<PublishedDocumentReadRateLimitOptions> rateLimitOptions,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        // Best-effort IP, the same fallback DemoEndpoints.HandleMintAsync uses - RemoteIpAddress is
        // null under some hosting and test setups, and "unknown" still buckets those together rather
        // than letting them past the limiter entirely.
        var requestIp = httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        var options = rateLimitOptions.Value;
        var limit = await rateLimiter.CheckAsync(
            new RateLimitKey($"document-read:ip:{requestIp}"),
            new RateLimitRule(options.PerIpCapacity, options.PerIpRefillPerSecond),
            cancellationToken);
        if (!limit.Allowed)
        {
            httpContext.Response.Headers.RetryAfter = ((int)Math.Ceiling(limit.RetryAfter.TotalSeconds)).ToString();
            return Results.Problem(
                title: "Too many requests", statusCode: StatusCodes.Status429TooManyRequests, type: "rate-limited");
        }

        var result = await handler.HandleAsync(query, cancellationToken);
        if (result.IsFailure)
        {
            return result.Error!.Value.ToProblem(httpContext);
        }

        var dto = result.Value;
        // `caching.md`'s own edge-cache guidance, restated at the one HTTP boundary this item builds:
        // a specific version never changes once published, so a shared/CDN cache in front of this API
        // may hold it far longer than "current" - api-design.md has no existing precedent for a
        // response Cache-Control header, so this is the first one, scoped to exactly the two routes
        // that need different values rather than a blanket default for every endpoint in this host.
        httpContext.Response.Headers.CacheControl = query.Version is { Length: > 0 }
            ? "public, max-age=86400, immutable"
            : "public, max-age=300";

        return Results.Ok(new DocumentVersionResponse(dto.DocumentKey, dto.Version, dto.Sequence, dto.Title, dto.Body, dto.PublishedAt));
    }

    /// <summary>The wire shape - its own type rather than <c>PublishedDocumentVersionDto</c> directly,
    /// per api-design.md, the same divergence-allowed boundary <c>MintedDemoTenantResponse</c>'s own
    /// remarks describe.</summary>
    public sealed record DocumentVersionResponse(
        string DocumentKey, string Version, int Sequence, string Title, string Body, DateTimeOffset PublishedAt);
}
