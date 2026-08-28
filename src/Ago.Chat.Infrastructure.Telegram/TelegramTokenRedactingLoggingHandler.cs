using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace Ago.Chat.Infrastructure.Telegram;

/// <summary>
/// Found live 2026-08-28, verifying `14-07` against a real bot: `Ago.Chat.Worker`'s own logs
/// (<c>kubectl logs</c>) printed the bot token in full, in plain text, on every request -
/// <c>GET https://api.telegram.org/bot&lt;real-token&gt;/getUpdates</c>. `HttpClientFactory`'s default
/// logging handlers (<c>LoggingHttpMessageHandler</c>/<c>LoggingScopeHttpMessageHandler</c>, added
/// automatically by <c>AddHttpClient</c>) redact header *values* unless the header name is explicitly
/// allow-listed - but they log <see cref="HttpRequestMessage.RequestUri"/> in full, always. That is
/// exactly safe for MAX (<c>MaxApiClient</c>, auth in an <c>Authorization</c> header, redacted by
/// default) and exactly unsafe for Telegram, whose auth
/// (<see cref="TelegramBotApiOptions"/>'s own remarks) travels in the URL path itself - the one
/// divergence from MAX's shape this item's own log already names as non-cosmetic, now with a second,
/// sharper consequence than the <c>Uri</c>-scheme bug <see cref="TelegramApiClient"/> already
/// documents.
///
/// <para>The fix registered here (<c>ChatModule</c> calls <c>RemoveAllLoggers()</c> on this client and
/// adds this handler in the default loggers' place) is not "stop logging Telegram calls" - that would
/// throw away the same operational visibility MAX's own client gets for free. It is "log the same
/// shape, with the one secret segment replaced." <see cref="RedactToken"/> replaces
/// <c>/bot&lt;anything&gt;/</c> with <c>/bot***/</c> by structural position (the first path segment,
/// which `14-07`'s own <c>TelegramApiClient.RelativePath</c> always builds as
/// <c>bot{token}/{method}</c>) rather than by matching the token's own shape - the token is exactly
/// the value that must never appear in the pattern used to find it.</para>
/// </summary>
public sealed class TelegramTokenRedactingLoggingHandler(ILogger<TelegramTokenRedactingLoggingHandler> logger)
    : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var redactedUri = RedactToken(request.RequestUri);
        var stopwatch = Stopwatch.StartNew();

        try
        {
            var response = await base.SendAsync(request, cancellationToken);
            logger.LogInformation(
                "Telegram API {Method} {RedactedUri} -> {StatusCode} in {ElapsedMs}ms",
                request.Method, redactedUri, (int)response.StatusCode, stopwatch.ElapsedMilliseconds);
            return response;
        }
        catch (Exception ex)
        {
            logger.LogInformation(
                ex, "Telegram API {Method} {RedactedUri} failed after {ElapsedMs}ms",
                request.Method, redactedUri, stopwatch.ElapsedMilliseconds);
            throw;
        }
    }

    /// <summary>Structural, not pattern-matched against the token's own value - see this class's own
    /// remarks on why. <paramref name="uri"/> is relative to <c>TelegramApiClient</c>'s
    /// <see cref="HttpClient.BaseAddress"/> (e.g. <c>./bot123:ABC/getUpdates?timeout=30</c>, the
    /// leading <c>./</c> from <c>TelegramApiClient.RelativePath</c>'s own <c>Uri</c>-scheme workaround)
    /// - the first path segment is always <c>bot&lt;token&gt;</c> by construction, so this replaces
    /// exactly that segment and leaves everything else (the method name, the query string) intact for
    /// diagnostics.</summary>
    public static string RedactToken(Uri? uri)
    {
        if (uri is null)
        {
            return "(no request uri)";
        }

        var text = uri.IsAbsoluteUri ? uri.PathAndQuery : uri.OriginalString;
        var segments = text.Split('/');

        for (var i = 0; i < segments.Length; i++)
        {
            if (segments[i].StartsWith("bot", StringComparison.Ordinal) && segments[i].Contains(':'))
            {
                segments[i] = "bot***";
            }
        }

        return string.Join('/', segments);
    }
}
