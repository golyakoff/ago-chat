using System.Net;
using System.Net.Http.Json;

namespace Ago.Chat.Infrastructure.MaxBot;

/// <summary>
/// `14-02`: the one class in this codebase that speaks MAX's own HTTP shape. Deliberately thin - no
/// retry, no timeout, no circuit breaker (<c>Ago.Chat.Domain.ChannelKind</c>'s adapter, wrapped in
/// <c>Ago.Chat.Module.Channels.ResilientInboundChannelAdapter</c>, is where all four of those live); this
/// class is written as if MAX always answers, matching <see cref="Application.Abstractions.IInboundChannelAdapter"/>'s
/// own remarks on why an adapter's implementation should never reference the resilience machinery
/// wrapping it.
///
/// <para><b>The terminal/transient split, made concrete for one real provider.</b>
/// <see cref="SendMessageAsync"/> returns a value for a response MAX answered but refused
/// (400/401/403/404 - a malformed request, a bad or revoked token, a blocked or unknown recipient) and
/// throws for everything else (5xx, a network fault, a timeout) - `resilience.md`'s own rule, applied
/// here for the first time against a real HTTP boundary rather than the stub
/// <c>ResilientInboundChannelAdapterTests</c> already proves the mechanism against. Which exact status
/// codes MAX uses for "this recipient is unreachable" is not in the public documentation this item could
/// reach; 400/401/403/404 is this item's own reasoned default (client-shaped errors are refusals,
/// server-shaped errors are transient), stated here so it is easy to correct once a real bot's real
/// error responses are observed.</para>
///
/// <para><b>`25-152`: the outbound <c>request_contact</c> button, and the identical caveat `25-151`
/// already names for the inbound side.</b> <see cref="SendMessageAsync"/>'s <c>requestContact</c>
/// parameter builds a <see cref="MaxOutboundAttachment"/> from MAX's documented outline (button
/// vocabulary and the "at most three per row" rule are both stated plainly in the public docs) rather
/// than a captured request/response pair - no live MAX bot or token was available while this item was
/// built, the same gap `25-151`'s own report and `MaxDtos.cs`'s own top-level note already state for the
/// inbound half. This is shipped as the best-effort implementation against the documented shape, flagged
/// here and in this item's own report, not blocked on and not claimed verified.</para>
/// </summary>
public sealed class MaxApiClient(HttpClient httpClient)
{
    private static readonly HttpStatusCode[] TerminalRefusalStatusCodes =
    [
        HttpStatusCode.BadRequest, HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden, HttpStatusCode.NotFound,
    ];

    public async Task<MaxSendResult> SendMessageAsync(
        string token, long chatId, string text, bool requestContact, CancellationToken cancellationToken)
    {
        // `25-152`: the inline-keyboard attachment is built here, at the wire boundary, rather than
        // handed in pre-built - MaxChannelAdapter has no business constructing a MaxOutboundAttachment
        // itself, the identical split TelegramApiClient.SendMessageAsync's own remarks establish for its
        // reply keyboard. One row, one button - MAX's own documented "at most three per row" ceiling
        // (MaxOutboundAttachment's own remarks) is not approached by this item's single button.
        var attachments = requestContact
            ? new MaxOutboundAttachment[]
            {
                new("inline_keyboard", new MaxInlineKeyboardPayload(
                    [[new MaxInlineKeyboardButton("request_contact", "Share phone number")]])),
            }
            : null;

        using var request = new HttpRequestMessage(HttpMethod.Post, $"messages?chat_id={chatId}")
        {
            Content = JsonContent.Create(new MaxSendMessageRequest(text, attachments)),
        };
        AddAuthorization(request, token);

        using var response = await httpClient.SendAsync(request, cancellationToken);

        if (response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadFromJsonAsync<MaxSendMessageResponse>(cancellationToken);
            return MaxSendResult.Sent(body?.Message?.Body?.Mid);
        }

        if (TerminalRefusalStatusCodes.Contains(response.StatusCode))
        {
            var errorText = await response.Content.ReadAsStringAsync(cancellationToken);
            return MaxSendResult.Refused($"MAX refused the message ({(int)response.StatusCode}): {Truncate(errorText)}");
        }

        var transientErrorText = await response.Content.ReadAsStringAsync(cancellationToken);
        throw new HttpRequestException(
            $"MAX API returned {(int)response.StatusCode} for POST /messages: {Truncate(transientErrorText)}",
            null, response.StatusCode);
    }

    /// <summary>
    /// MAX's production mechanism (this item's backlog note - webhook, not long polling, is what MAX's
    /// own documentation calls suitable for production). Throws <see cref="MaxSubscriptionRejectedException"/>
    /// on a clear rejection - the caller (<c>Ago.Chat.Api</c>'s registration endpoint) uses that
    /// specifically to decide whether to revoke the credential it just created.
    /// </summary>
    public async Task SubscribeWebhookAsync(string token, Uri callbackUrl, string secret, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "subscriptions")
        {
            Content = JsonContent.Create(new MaxSubscribeRequest(callbackUrl.ToString(), secret, ["message_created"])),
        };
        AddAuthorization(request, token);

        using var response = await httpClient.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var errorText = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new MaxSubscriptionRejectedException(
                $"MAX refused the webhook subscription ({(int)response.StatusCode}): {Truncate(errorText)}");
        }
    }

    /// <summary>
    /// `25-147`: MAX's own <c>GET /me</c> - stale doc note this item corrects, not a new discovery about
    /// MAX itself: <c>Ago.Chat.Api.Channels.MaxChannelEndpoints</c>' own remarks used to claim MAX exposes
    /// only <c>POST /subscriptions</c> and <c>GET /updates</c>, which was wrong even before this item -
    /// MAX's Bot API has always had this method, needing only the bot token
    /// <see cref="AddAuthorization"/> already attaches. Mirrors <see cref="SubscribeWebhookAsync"/>'s own
    /// shape (a bare authorized GET/POST, no query parameters), but modelled as a result the caller
    /// inspects rather than a void call that throws - the identical "no side effect, so return a result
    /// instead of throwing on a terminal refusal" reasoning <see cref="TelegramApiClient.GetMeAsync"/>'s
    /// own remarks give for itself, since this call, like Telegram's, is a plain read a tenant merely
    /// looking at the connect screen should not be able to break anything by triggering.
    ///
    /// <para><b>Called best-effort, never as a connect-time gate.</b> Unlike Telegram (where `getMe` was
    /// already a hard gate this item merely rides along on) and unlike VK/WhatsApp (whose own discovery
    /// calls run *before* <c>RegisterChannelCredentialHandler</c> and can refuse a bad token outright),
    /// this is a genuinely new provider round trip MAX's connect flow never made before. Making it a hard
    /// gate would be a real behaviour change this item was not asked to make - a MAX credential with no
    /// public webhook base URL configured is already accepted "on the strength of nothing yet"
    /// (<see cref="SubscribeWebhookAsync"/>'s own remarks on the compose-loop skip), and this call must
    /// not become a second, stricter gate that flow does not have today. So a refusal or a transient
    /// fault here costs the tenant nothing but a missing public handle, never a failed connect - see
    /// <c>MaxChannelEndpoints.HandleConnectAsync</c>'s own try/catch around this call.</para>
    /// </summary>
    public async Task<MaxGetMeResult> GetMeAsync(string token, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "me");
        AddAuthorization(request, token);

        using var response = await httpClient.SendAsync(request, cancellationToken);

        if (response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadFromJsonAsync<MaxGetMeResponse>(cancellationToken);
            return MaxGetMeResult.Success(body?.Username);
        }

        if (TerminalRefusalStatusCodes.Contains(response.StatusCode))
        {
            var errorText = await response.Content.ReadAsStringAsync(cancellationToken);
            return MaxGetMeResult.Refused($"MAX refused the token ({(int)response.StatusCode}): {Truncate(errorText)}");
        }

        var transientErrorText = await response.Content.ReadAsStringAsync(cancellationToken);
        throw new HttpRequestException(
            $"MAX API returned {(int)response.StatusCode} for GET /me: {Truncate(transientErrorText)}",
            null, response.StatusCode);
    }

    /// <summary>
    /// `25-161`: fetches the actual bytes of an inbound photo attachment - the download half of this
    /// item's diagnosis, alongside <see cref="MaxInboundMessageParser"/>'s parsing half.
    /// <see cref="MaxAttachmentPayload.Url"/> is, per MAX's own documented outline, "a direct link to
    /// image in internet" - a plain HTTPS URL, not a second authenticated MAX API call, so this method
    /// does not go through <see cref="AddAuthorization"/> at all: <paramref name="url"/> already comes
    /// from an update this codebase authenticated on the way in (the webhook's own secret header, or a
    /// long-poll answered against a real bot token), and attaching this bot's own token as a bare
    /// header to a request against an arbitrary MAX-supplied host would leak it to whatever that host
    /// actually is - a needless widening of what this token is ever sent to, for a call that does not
    /// need it.
    ///
    /// <para>Only an absolute, <c>https</c> URL is ever fetched - a defensive floor this class's own
    /// terminal/transient split does not otherwise need (every other method here calls a URL this
    /// codebase itself constructed), because this is the one method whose target is a value read
    /// straight off an inbound payload. Anything else - relative, <c>http</c>, or another scheme
    /// entirely - is treated the same as "MAX gave us nothing usable," not attempted.</para>
    ///
    /// <para>Shares <see cref="SendMessageAsync"/>'s own terminal/transient split
    /// (<see cref="TerminalRefusalStatusCodes"/>): a 400/401/403/404 is <see langword="null"/> - this
    /// image cannot be fetched, full stop, no retry would change that - and everything else throws, so
    /// a transient failure here rides the same retry this codebase already gives an inbound webhook
    /// (MAX's own 200-or-retry contract) or long-poll iteration (<see cref="MaxLongPollingService"/>'s
    /// own backoff-and-retry catch), rather than being swallowed as if it were a permanent refusal.</para>
    /// </summary>
    public async Task<MaxImageDownloadResult?> DownloadImageAsync(string url, CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
        {
            return null;
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        using var response = await httpClient.SendAsync(request, cancellationToken);

        if (response.IsSuccessStatusCode)
        {
            var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
            var contentType = response.Content.Headers.ContentType?.MediaType ?? "application/octet-stream";
            return new MaxImageDownloadResult(bytes, contentType);
        }

        if (TerminalRefusalStatusCodes.Contains(response.StatusCode))
        {
            return null;
        }

        throw new HttpRequestException(
            $"MAX image download returned {(int)response.StatusCode} for a URL this update supplied.",
            null, response.StatusCode);
    }

    /// <summary>The dev-only loop (`14-02`'s backlog note): MAX's own documentation calls this
    /// "limited by speed and event retention" - fine for the local compose loop, which this project's
    /// runbook is the only caller of.</summary>
    public async Task<MaxUpdatesEnvelope> GetUpdatesAsync(
        string token, long? marker, int timeoutSeconds, CancellationToken cancellationToken)
    {
        var query = marker is { } m ? $"updates?timeout={timeoutSeconds}&marker={m}" : $"updates?timeout={timeoutSeconds}";
        using var request = new HttpRequestMessage(HttpMethod.Get, query);
        AddAuthorization(request, token);

        using var response = await httpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();

        var envelope = await response.Content.ReadFromJsonAsync<MaxUpdatesEnvelope>(cancellationToken);
        return envelope ?? new MaxUpdatesEnvelope([], marker);
    }

    // `14-02`'s backlog note: "the token travels in the Authorization header, not a query parameter" -
    // confirmed against MAX's own documentation, but with no confirmed scheme prefix (no "Bearer "
    // shown in any source this item could reach), so the raw token is sent as the header's entire
    // value. TryAddWithoutValidation rather than the Authorization: AuthenticationHeaderValue
    // constructor - that type demands a well-formed "scheme value" pair and would reject a bare token.
    private static void AddAuthorization(HttpRequestMessage request, string token) =>
        request.Headers.TryAddWithoutValidation("Authorization", token);

    private static string Truncate(string text) => text.Length > 500 ? text[..500] : text;
}

public sealed record MaxSendResult(bool Success, string? ProviderMessageId, string? RefusalReason)
{
    public static MaxSendResult Sent(string? providerMessageId) => new(true, providerMessageId, null);

    public static MaxSendResult Refused(string reason) => new(false, null, reason);
}

public sealed record MaxGetMeResult(bool Ok, string? Username, string? RefusalReason)
{
    public static MaxGetMeResult Success(string? username) => new(true, username, null);

    public static MaxGetMeResult Refused(string reason) => new(false, null, reason);
}

/// <summary>`25-161`: what <see cref="MaxApiClient.DownloadImageAsync"/> actually found - the real
/// bytes, and the content type MAX's own response declared for them (never the declared type of
/// anything the sending visitor claimed; this is read off the HTTP response itself, the same "verify,
/// never trust the claim" posture <c>ConfirmAttachmentHandler</c>'s own HEAD-verify already holds for a
/// widget upload).</summary>
public sealed record MaxImageDownloadResult(byte[] Content, string ContentType);
