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
/// </summary>
public sealed class MaxApiClient(HttpClient httpClient)
{
    private static readonly HttpStatusCode[] TerminalRefusalStatusCodes =
    [
        HttpStatusCode.BadRequest, HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden, HttpStatusCode.NotFound,
    ];

    public async Task<MaxSendResult> SendMessageAsync(
        string token, long chatId, string text, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"messages?chat_id={chatId}")
        {
            Content = JsonContent.Create(new MaxSendMessageRequest(text)),
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
