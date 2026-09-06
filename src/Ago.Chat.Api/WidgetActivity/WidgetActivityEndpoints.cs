using Ago.Chat.Application.Abstractions;
using Ago.Chat.Application.UseCases.GetSiteByPublicKey;
using Ago.Chat.Domain;
using Ago.Platform.Abstractions;
using Ago.Platform.Kernel;
using Microsoft.Extensions.Options;

namespace Ago.Chat.Api.WidgetActivity;

/// <summary>
/// `23-07`: the funnel's first two numbers, straight from the widget - `docs/backlog/23-07-*.md`'s
/// own Scope: "one unauthenticated `POST` taking the site's public key and an event kind (`load` |
/// `open`) and nothing else." Modeled on <see cref="Auth.AuthEndpoints.HandleVisitorSessionAsync"/>
/// almost line for line - same cache-aside site lookup, same layer-2 origin check, same
/// <see cref="ISiteInstallationSignalRepository.RecordSightingAsync"/> call - because this endpoint is
/// deliberately *not* a second notion of "seen": the item's own "Where this is easy to get wrong" #4.
/// The one real difference is the rate limiter's own key, per IP rather than per site
/// (<see cref="WidgetActivityBeaconRateLimitOptions"/>'s own remarks explain why).
///
/// <para><b>Writes nothing synchronously.</b> <see cref="IWidgetActivityRecorder.RecordLoad"/>/
/// <see cref="IWidgetActivityRecorder.RecordOpen"/> are bare in-memory increments
/// (<c>WidgetActivityAccumulator</c>'s own remarks) - the only I/O this method ever performs is the
/// rate-limit check, the cached site lookup, and (throttled to once a minute per site, exactly as the
/// mint endpoint already is) the sighting/refusal write below.</para>
///
/// <para><b>A refused origin counts nothing and still records the refusal</b> - the item's own
/// Done-when, checked before either counter is ever touched: <see cref="HandleAsync"/> returns from
/// the origin-check branch before reaching either <c>RecordLoad</c>/<c>RecordOpen</c> call, the
/// identical ordering <c>AuthEndpoints.HandleVisitorSessionAsync</c> already uses for the identical
/// reason.</para>
/// </summary>
public static class WidgetActivityEndpoints
{
    public static void MapWidgetActivityEndpoints(this WebApplication app)
    {
        app.MapPost("/api/v1/widget-activity", HandleAsync);
    }

    public static async Task<IResult> HandleAsync(
        WidgetActivityBeaconRequest request,
        GetSiteConfigByPublicKeyHandler getSite,
        ISiteInstallationSignalRepository installationSignals,
        IWidgetActivityRecorder activity,
        IRateLimiter rateLimiter,
        IOptions<WidgetActivityBeaconRateLimitOptions> rateLimitOptions,
        IClock clock,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        // `23-07`'s own Out of scope: "Anything in the beacon body beyond the site key and the kind."
        // An unrecognised kind is refused outright rather than silently ignored - this is a public,
        // unauthenticated endpoint, so a malformed or unexpected value is exactly as likely to be a
        // caller bug as an attacker probing, and neither should be counted as anything.
        if (!TryParseKind(request.Kind, out var kind))
        {
            return Results.Problem(
                title: "Unknown beacon kind", statusCode: StatusCodes.Status400BadRequest, type: "widget-activity-invalid-kind");
        }

        // Per IP, checked before the site lookup - see WidgetActivityBeaconRateLimitOptions' own
        // remarks for why per IP rather than per site, and AuthEndpoints.HandleVisitorSessionAsync's
        // own comment for why a rate check comes before any lookup at all: a bad or unknown public
        // key still costs the caller a token.
        var requestIp = httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        var options = rateLimitOptions.Value;
        var limit = await rateLimiter.CheckAsync(
            new RateLimitKey($"widget-activity:ip:{requestIp}"),
            new RateLimitRule(options.PerIpCapacity, options.PerIpRefillPerSecond),
            cancellationToken);
        if (!limit.Allowed)
        {
            httpContext.Response.Headers.RetryAfter = ((int)Math.Ceiling(limit.RetryAfter.TotalSeconds)).ToString();
            return Results.Problem(
                title: "Too many requests", statusCode: StatusCodes.Status429TooManyRequests, type: "rate-limited");
        }

        // `3-04`: the same cache-aside site lookup the mint/renew endpoints already use - this is a
        // beacon fired on every mount, so it inherits their reasoning for not hitting Postgres per call.
        var site = await getSite.HandleAsync(new GetSiteConfigByPublicKey(request.PublicKey), cancellationToken);
        if (site is null)
        {
            return Results.Problem(
                title: "Site not found", statusCode: StatusCodes.Status404NotFound, type: "site-not-found");
        }

        var siteId = new SiteId(site.SiteId);
        var now = clock.UtcNow;

        // `5-01`, layer 2 - identical to the mint endpoint's own check, and for the identical reason:
        // CORS only proved this Origin belongs to *some* site, not this one.
        var origin = httpContext.Request.Headers.Origin.ToString();
        if (!string.IsNullOrEmpty(origin) && !site.AllowedOrigins.Contains(origin))
        {
            // `23-06`'s own refusal recording, reused rather than duplicated - this item's own
            // Done-when: "A beacon from a refused origin counts nothing and records the refusal."
            await installationSignals.RecordRefusedOriginAsync(siteId, origin, now, cancellationToken);
            return Results.Problem(
                title: "Origin not allowed for this site", statusCode: StatusCodes.Status403Forbidden, type: "origin-not-allowed");
        }

        // `23-06`'s own sighting write, reused rather than a second notion of "seen" - this is what
        // closes that item's stated returning-visitor gap: a beacon fires on every mount regardless of
        // whether the token needed a renewal call this time.
        await installationSignals.RecordSightingAsync(siteId, now, cancellationToken);

        switch (kind)
        {
            case WidgetActivityEventKind.Load:
                activity.RecordLoad(siteId, now);
                break;
            case WidgetActivityEventKind.Open:
                activity.RecordOpen(siteId, now);
                break;
        }

        return Results.NoContent();
    }

    private static bool TryParseKind(string? raw, out WidgetActivityEventKind kind)
    {
        switch (raw)
        {
            case "load":
                kind = WidgetActivityEventKind.Load;
                return true;
            case "open":
                kind = WidgetActivityEventKind.Open;
                return true;
            default:
                kind = default;
                return false;
        }
    }

    public sealed record WidgetActivityBeaconRequest(string PublicKey, string Kind);
}
