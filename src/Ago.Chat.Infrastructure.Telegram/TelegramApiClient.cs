using System.Net;
using System.Net.Http.Json;

namespace Ago.Chat.Infrastructure.Telegram;

/// <summary>
/// `14-07`: the one class in this codebase that speaks Telegram's own HTTP shape. Deliberately thin -
/// no retry, no timeout, no circuit breaker, no proxy-awareness of its own (that is
/// <c>ChatModule</c>'s job when it builds this class's <see cref="HttpClient"/>; see
/// <see cref="TelegramProxyOptions"/>'s own remarks on why that split is deliberate) - written as if
/// Telegram always answers, the identical discipline <c>MaxApiClient</c>'s own remarks describe.
///
/// <para><b>Auth shape - confirmed against Telegram's own documentation, and different from MAX.</b>
/// The token travels in the URL path (<c>{BaseUrl}/bot&lt;token&gt;/{method}</c>), never in a header -
/// see <see cref="TelegramBotApiOptions"/>'s own remarks for the full comparison with MAX's
/// <c>Authorization</c>-header shape.</para>
///
/// <para><b>The terminal/transient split, made concrete for Telegram.</b> <see cref="SendMessageAsync"/>
/// returns a value for a response Telegram answered but refused (400/401/403/404 - a malformed request,
/// a bad or revoked token, a chat the bot was blocked from or that no longer exists) and throws for
/// everything else (429, 5xx, a network fault, a timeout) - `resilience.md`'s own rule, the same
/// reasoned default <c>MaxApiClient</c>'s own remarks state for MAX (client-shaped errors are refusals,
/// server-shaped errors are transient). Telegram's own documentation confirms <c>error_code</c> tracks
/// the HTTP status directly, but does not enumerate which codes mean what beyond the well-known ones
/// used here (401 an invalid token, 403 most commonly "bot was blocked by the user", 404 an unknown
/// chat or method, 400 a malformed request) - this item's own reasoned default, stated here so it is
/// easy to correct once a real bot's real error responses are observed. <c>429 Too Many Requests</c> is
/// deliberately <em>not</em> terminal: Telegram's own rate limiting is exactly the kind of transient,
/// retry-worthy condition the wrapping resilience pipeline's backoff exists for, unlike a
/// permanently-blocked chat.</para>
/// </summary>
public sealed class TelegramApiClient(HttpClient httpClient)
{
    private static readonly HttpStatusCode[] TerminalRefusalStatusCodes =
    [
        HttpStatusCode.BadRequest, HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden, HttpStatusCode.NotFound,
    ];

    public async Task<TelegramSendResult> SendMessageAsync(
        string token, long chatId, string text, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, RelativePath($"bot{token}/sendMessage"))
        {
            Content = JsonContent.Create(new TelegramSendMessageRequest(chatId, text)),
        };

        using var response = await httpClient.SendAsync(request, cancellationToken);

        if (response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadFromJsonAsync<TelegramApiResponse<TelegramMessage>>(cancellationToken);
            return TelegramSendResult.Sent(body?.Result?.MessageId?.ToString());
        }

        if (TerminalRefusalStatusCodes.Contains(response.StatusCode))
        {
            var errorText = await response.Content.ReadAsStringAsync(cancellationToken);
            return TelegramSendResult.Refused($"Telegram refused the message ({(int)response.StatusCode}): {Truncate(errorText)}");
        }

        var transientErrorText = await response.Content.ReadAsStringAsync(cancellationToken);
        throw new HttpRequestException(
            $"Telegram API returned {(int)response.StatusCode} for POST sendMessage: {Truncate(transientErrorText)}",
            null, response.StatusCode);
    }

    /// <summary>
    /// This channel's one and only inbound mechanism - see <see cref="TelegramBotApiOptions"/>'s own
    /// remarks on why there is no webhook counterpart to pair with it the way MAX's own long-poll and
    /// webhook receiver coexist. <paramref name="offset"/> is Telegram's own acknowledgement cursor -
    /// passing <c>last_update_id + 1</c> tells Telegram every earlier update was processed and may be
    /// dropped, the same "pass a cursor forward" shape as MAX's own <c>marker</c>, with the opposite
    /// direction of intent: MAX's marker is what MAX hands back to say "resume from here", Telegram's
    /// offset is what the caller sends to say "you may forget up to here."
    /// </summary>
    public async Task<TelegramUpdatesResult> GetUpdatesAsync(
        string token, long? offset, int timeoutSeconds, CancellationToken cancellationToken)
    {
        var query = offset is { } o
            ? $"bot{token}/getUpdates?timeout={timeoutSeconds}&offset={o}"
            : $"bot{token}/getUpdates?timeout={timeoutSeconds}";
        using var request = new HttpRequestMessage(HttpMethod.Get, RelativePath(query));

        using var response = await httpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();

        var body = await response.Content.ReadFromJsonAsync<TelegramApiResponse<IReadOnlyList<TelegramUpdate>>>(cancellationToken);
        return new TelegramUpdatesResult(body?.Result ?? []);
    }

    /// <summary>
    /// Telegram's own <c>GET /bot&lt;token&gt;/getMe</c> - used by <c>Ago.Chat.Api</c>'s connect
    /// endpoint to reject a bad token immediately, the same "fail fast at registration" UX
    /// <c>MaxApiClient.SubscribeWebhookAsync</c> gives MAX, reached a different way: MAX's equivalent
    /// call has a required side effect (subscribing a webhook) and so is modelled as a void call that
    /// throws on rejection; <c>getMe</c> has no side effect at all - it is a plain read - so this
    /// returns a result the caller inspects instead of throwing on a terminal refusal. A transient fault
    /// (Telegram down, or this deployment's own relay down) still throws, deliberately not folded into
    /// "refused": the caller's own rollback (revoking the credential it just created) is correct only
    /// for a token Telegram has actually looked at and rejected, never for a call that could not reach
    /// Telegram at all - revoking a possibly-good credential because of an outage the operator did
    /// nothing to cause would be the wrong failure mode.
    /// </summary>
    public async Task<TelegramGetMeResult> GetMeAsync(string token, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, RelativePath($"bot{token}/getMe"));

        using var response = await httpClient.SendAsync(request, cancellationToken);

        if (response.IsSuccessStatusCode)
        {
            return TelegramGetMeResult.Success();
        }

        if (TerminalRefusalStatusCodes.Contains(response.StatusCode))
        {
            var errorText = await response.Content.ReadAsStringAsync(cancellationToken);
            return TelegramGetMeResult.Refused($"Telegram refused the token ({(int)response.StatusCode}): {Truncate(errorText)}");
        }

        var transientErrorText = await response.Content.ReadAsStringAsync(cancellationToken);
        throw new HttpRequestException(
            $"Telegram API returned {(int)response.StatusCode} for GET getMe: {Truncate(transientErrorText)}",
            null, response.StatusCode);
    }

    /// <summary>
    /// Found live while writing <c>TelegramApiClientTests</c>, 2026-08-28: a bare
    /// <c>$"bot{token}/sendMessage"</c> handed to <see cref="HttpRequestMessage"/>'s string constructor
    /// throws <see cref="NotSupportedException"/> ("The 'bot123456' scheme is not supported") the moment
    /// a real bot token is used, because a real Telegram token itself contains a colon
    /// (<c>123456:AAExampleNotARealToken</c>) - and <see cref="Uri"/>'s own parser reads anything before
    /// the first <c>/</c> that is followed by a colon as a URI *scheme*, exactly the same ambiguity RFC
    /// 3986 itself calls out for a relative reference whose first segment contains a colon. The request
    /// is then treated as an absolute URI with an unrecognised scheme instead of a path to combine with
    /// <see cref="HttpClient.BaseAddress"/>, and <see cref="System.Net.Http.SocketsHttpHandler"/> rejects
    /// it outright. This is precisely the auth-in-the-URL-path shape <see cref="TelegramBotApiOptions"/>'
    /// own remarks call out as the genuine divergence from MAX's header-based auth - and it is also the
    /// one place that divergence turned out to have a sharp edge MAX's own code never had to handle. The
    /// fix is the standard one for this exact ambiguity: prefixing the path with <c>./</c> forces
    /// <see cref="Uri"/> to parse it as a relative reference unconditionally.
    /// </summary>
    private static string RelativePath(string path) => $"./{path}";

    private static string Truncate(string text) => text.Length > 500 ? text[..500] : text;
}

public sealed record TelegramSendResult(bool Success, string? ProviderMessageId, string? RefusalReason)
{
    public static TelegramSendResult Sent(string? providerMessageId) => new(true, providerMessageId, null);

    public static TelegramSendResult Refused(string reason) => new(false, null, reason);
}

public sealed record TelegramUpdatesResult(IReadOnlyList<TelegramUpdate> Updates);

public sealed record TelegramGetMeResult(bool Ok, string? RefusalReason)
{
    public static TelegramGetMeResult Success() => new(true, null);

    public static TelegramGetMeResult Refused(string reason) => new(false, reason);
}
