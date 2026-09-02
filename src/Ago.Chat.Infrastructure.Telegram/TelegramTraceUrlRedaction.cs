using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using OpenTelemetry.Instrumentation.Http;

namespace Ago.Chat.Infrastructure.Telegram;

/// <summary>
/// The tracing half of the same leak <see cref="TelegramTokenRedactingLoggingHandler"/> closed for
/// logging, found 2026-09-02 while auditing `14-07` against the telemetry this deployment actually
/// runs. Telegram's auth travels in the URL path (<see cref="TelegramBotApiOptions"/>' own remarks),
/// and OpenTelemetry's HttpClient instrumentation records the outgoing URL as the span attribute
/// <c>url.full</c> - so every <c>sendMessage</c>/<c>getUpdates</c>/<c>getMe</c> span carried the bot
/// token in plain text into this deployment's own Jaeger. Same secret, same cause, a completely
/// different pipe: OTel does not observe the <see cref="System.Net.Http.HttpClient"/> handler chain at
/// all - it listens to <c>System.Net.Http</c>'s own <c>DiagnosticSource</c> from inside
/// <c>SocketsHttpHandler</c>, *below* every <see cref="System.Net.Http.DelegatingHandler"/> - which is
/// why <c>ChatModule</c>'s <c>RemoveAllLoggers()</c> plus a redacting handler did nothing for it, and
/// why the fix has to be an instrumentation-level hook rather than another handler.
///
/// <para><b>What the instrumentation redacts on its own, checked rather than assumed.</b>
/// OpenTelemetry.Instrumentation.Http 1.18.0 redacts the URI's <c>userinfo</c> component and the
/// *values* of the query string (that one switchable off with <c>DisableUrlQueryRedaction</c>). It
/// never touches the path. That is exactly right for every other outbound client in this codebase and
/// exactly wrong for Telegram - the same asymmetry with MAX's header-based auth that
/// <see cref="TelegramTokenRedactingLoggingHandler"/>'s own remarks describe.</para>
///
/// <para><b>Why enrichment and not a filter.</b> The instrumentation's other hook,
/// <c>FilterHttpRequestMessage</c>, would drop the span entirely - trading a leaked secret for no
/// outbound-call telemetry at all on this deployment's one channel that polls continuously. Enrichment
/// runs after the instrumentation has written its own tags and can overwrite one of them, so the span
/// keeps its method, its status, its duration and its host, and loses only the token segment. There is
/// no third option worth weighing: OTel has no per-named-<see cref="System.Net.Http.HttpClient"/>
/// instrumentation configuration - <c>HttpClientTraceInstrumentationOptions</c> is process-wide - so
/// "turn URL capture off for just <see cref="TelegramApiClient"/>'s client" is not a thing that can be
/// registered, and turning it off process-wide would blind every other client.</para>
///
/// <para><b>Why this stays in `Ago.Chat.*` and not in `Ago.Platform.Observability`.</b> Redacting a
/// path segment because a *particular provider* puts a token there is product knowledge, not platform
/// knowledge - `clean-architecture.md`'s qualifying rules, and CLAUDE.md's "the platform must never
/// reference a product". The platform's own <c>AddHttpClientInstrumentation()</c> stays a plain,
/// unopinionated default; this project, which by its own csproj remarks is the one place allowed to
/// know Telegram's URL shape, adds the one thing that shape requires.</para>
/// </summary>
public static class TelegramTraceUrlRedaction
{
    /// <summary>OpenTelemetry's stable HTTP semantic-convention attribute for the full outgoing URL -
    /// the one tag this class overwrites. Named here rather than inlined because it is the exact
    /// contract with the instrumentation: if a future OTel version renames it, this constant is the
    /// single place that changes, and <c>TelegramTraceUrlRedactionTests</c>' end-to-end fact is what
    /// notices.</summary>
    public const string UrlFullTagName = "url.full";

    /// <summary>
    /// Registers the enrichment on the process-wide
    /// <see cref="HttpClientTraceInstrumentationOptions"/> every <c>AddHttpClientInstrumentation()</c>
    /// call reads through <c>IOptionsMonitor</c>. Safe to call from a host that wires no tracing at
    /// all - it only configures an options object nothing will then resolve.
    ///
    /// <para>The existing delegate is chained rather than replaced: nothing sets
    /// <see cref="HttpClientTraceInstrumentationOptions.EnrichWithHttpRequestMessage"/> today, and a
    /// second registration that silently dropped the first is precisely the wiring mistake that would
    /// reopen this leak without failing anything.</para>
    /// </summary>
    public static IServiceCollection AddTelegramTokenTraceRedaction(this IServiceCollection services)
    {
        services.Configure<HttpClientTraceInstrumentationOptions>(options =>
        {
            var previous = options.EnrichWithHttpRequestMessage;
            options.EnrichWithHttpRequestMessage = (activity, request) =>
            {
                previous?.Invoke(activity, request);
                RedactBotTokenInUrlFull(activity, request);
            };
        });

        return services;
    }

    /// <summary>
    /// Rewrites <see cref="UrlFullTagName"/> in place when - and only when - the request's path
    /// carries a Telegram bot token, reusing <see cref="TelegramTokenRedactingLoggingHandler.RedactToken"/>
    /// so both signals redact by the identical structural rule and can never drift apart.
    ///
    /// <para>The gate is structural (does the path contain a <c>bot&lt;something&gt;:&lt;something&gt;</c>
    /// segment), not "is this host <c>api.telegram.org</c>". Two reasons, both about failing in the safe
    /// direction: it keeps working if a deployment points <see cref="TelegramBotApiOptions.BaseUrl"/> at
    /// a mirror or a relay's own hostname, and it needs no injected options to decide - a hook that runs
    /// on every outbound span in the process should not be able to fail open because a *different*
    /// options object was misconfigured. The cost is symmetric and small: a non-Telegram URL that
    /// happened to contain such a segment would lose it from its span, which is the harmless direction
    /// to be wrong in.</para>
    /// </summary>
    public static void RedactBotTokenInUrlFull(Activity activity, HttpRequestMessage request)
    {
        var uri = request.RequestUri;
        if (uri is null || !uri.IsAbsoluteUri)
        {
            return;
        }

        var redacted = TelegramTokenRedactingLoggingHandler.RedactToken(uri);
        if (string.Equals(redacted, uri.PathAndQuery, StringComparison.Ordinal))
        {
            // Nothing in this path looked like a bot token - leave the span exactly as the
            // instrumentation wrote it, so this hook is invisible to every other client.
            return;
        }

        activity.SetTag(UrlFullTagName, uri.GetLeftPart(UriPartial.Authority) + redacted);
    }
}
