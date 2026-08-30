using System.Net.Http.Json;

namespace Ago.Chat.Infrastructure.WhatsApp;

/// <summary>
/// `14-10`: the one class in this codebase that speaks Meta's own Graph API shape for WhatsApp - deliberately
/// thin, no retry, no timeout, no circuit breaker (<c>ChannelKind.WhatsApp</c>'s adapter, wrapped in
/// <c>Ago.Chat.Module.Channels.ResilientInboundChannelAdapter</c>, is where all four of those live for
/// <see cref="SendMessageAsync"/>), the identical division <c>MaxApiClient</c>/<c>VkApiClient</c> already
/// establish and <see cref="Application.Abstractions.IInboundChannelAdapter"/>'s own remarks require.
///
/// <para><b>A fourth, genuinely distinct outcome shape - not a repeat of MAX's, Telegram's or VK's own.</b>
/// MAX/Telegram read the HTTP status code alone to tell success from failure. VK always answers HTTP 200
/// and puts the outcome entirely in the JSON body. WhatsApp does neither in isolation: a failed call
/// comes back a real non-200 status (confirmed from Meta's own documentation - Graph API errors are not
/// disguised as 200s the way VK's are), <em>and</em> the terminal/transient distinction still has to be
/// read from the JSON body's own numeric <c>error.code</c>, because a non-success status alone does not
/// say whether retrying would help. So this class checks <see cref="HttpResponseMessage.IsSuccessStatusCode"/>
/// first (closer to MAX's shape) and then, on failure, parses the body's own error code to decide
/// Refused-vs-thrown (closer to VK's shape) - the union of both precedents, not a rediscovery of
/// either.</para>
/// </summary>
public sealed class WhatsAppApiClient(HttpClient httpClient)
{
    /// <summary>
    /// <see cref="Application.Abstractions.IInboundChannelAdapter"/>'s own terminal/transient split, made
    /// concrete for WhatsApp's real numeric error-code taxonomy - confirmed live against Meta's own
    /// Cloud API error-codes reference documentation, 2026-08-30 (developers.facebook.com was reachable
    /// from this environment; unlike `14-02`'s MAX and `14-08`'s VK, this item did not need a
    /// third-party or SDK-source fallback for this part). Every code below is one this item could
    /// actually read Meta's own stated meaning for; nothing was guessed.
    ///
    /// <para><c>100</c> (unsupported/misspelled parameter), <c>131008</c>/<c>131009</c> (missing/invalid
    /// parameter - template-message-shaped codes this item's own text-only send path should never
    /// trigger, kept terminal defensively rather than left unclassified), <c>131021</c> (sender and
    /// recipient are the same number), <c>131026</c> (recipient is not a WhatsApp user, or an
    /// incompatible client), <c>131037</c> (this number's own display name has not been approved),
    /// <c>131050</c> (the user opted out) are all recipient/account problems a retry would never fix -
    /// the identical "client-shaped errors are refusals" reasoning <c>MaxApiClient</c>'s own
    /// <c>TerminalRefusalStatusCodes</c> states for HTTP status codes instead of a body code.</para>
    ///
    /// <para><c>131047</c> ("more than 24 hours have passed since the recipient last replied") is the one
    /// code this item's own backlog note names by name - the 24-hour customer-service-window constraint
    /// no MAX/Telegram/VK send ever had to consider. It is terminal, not transient: no amount of
    /// retrying turns a free-form message sent outside the window into one Meta will accept, because the
    /// fix is a pre-approved message template, which this item deliberately does not build
    /// (<see cref="WhatsAppChannelAdapter"/>'s own remarks on that scope decision). <c>131049</c>
    /// ("not delivered to maintain healthy ecosystem engagement" - Meta's own automated
    /// engagement-quality throttle, aimed at one specific message rather than the account) is grouped
    /// with it for the identical reason: nothing about resending the identical message changes Meta's own
    /// verdict on it.</para>
    ///
    /// <para>Everything else - including the documented rate-limit codes <c>4</c> (app-level),
    /// <c>80007</c> (WhatsApp Business Account-level), <c>130429</c> (Cloud API throughput) and
    /// <c>131056</c> (too many messages to one recipient in a short window), and the documented transient
    /// codes <c>2</c> (temporary downtime/overload), <c>131016</c> (service temporarily unavailable) and
    /// <c>131057</c> (account in maintenance) - defaults to transient (thrown), the same "err toward
    /// retrying, not toward silently giving up on a real outage" default MAX's and VK's own classes each
    /// apply to a code outside their own confirmed list. A generic OAuth/auth failure (Meta's own
    /// platform-wide code <c>190</c>, documented across every Graph API product rather than found on the
    /// WhatsApp-specific error page this item's own research used) would also belong in the terminal set -
    /// named here rather than added silently, since this item's own citation for it is general Graph API
    /// knowledge, not the same page the codes above were read from.</para>
    /// </summary>
    private static readonly HashSet<int> TerminalRefusalErrorCodes =
        [100, 131008, 131009, 131021, 131026, 131037, 131047, 131049, 131050];

    public async Task<WhatsAppSendResult> SendMessageAsync(
        string token, string phoneNumberId, string to, string text, CancellationToken cancellationToken)
    {
        var request = new WhatsAppSendMessageRequest("whatsapp", "individual", to, "text", new WhatsAppMessageText(text));

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, $"{phoneNumberId}/messages")
        {
            Content = JsonContent.Create(request),
        };
        httpRequest.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);

        using var response = await httpClient.SendAsync(httpRequest, cancellationToken);

        if (response.IsSuccessStatusCode)
        {
            var success = await response.Content.ReadFromJsonAsync<WhatsAppSendMessageResponse>(cancellationToken);
            var messageId = success?.Messages?.FirstOrDefault()?.Id;
            return WhatsAppSendResult.Sent(messageId);
        }

        var envelope = await response.Content.ReadFromJsonAsync<WhatsAppErrorEnvelope>(cancellationToken);
        if (envelope?.Error is { } error)
        {
            if (TerminalRefusalErrorCodes.Contains(error.Code))
            {
                return WhatsAppSendResult.Refused($"WhatsApp refused the message (error {error.Code}): {Truncate(error.Message)}");
            }

            throw new HttpRequestException(
                $"WhatsApp API returned error {error.Code} (HTTP {(int)response.StatusCode}) for the messages endpoint: {Truncate(error.Message)}");
        }

        throw new HttpRequestException(
            $"WhatsApp API returned HTTP {(int)response.StatusCode} for the messages endpoint with no parseable error body.");
    }

    /// <summary>
    /// <c>GET /{version}/{phone-number-id}</c>, confirmed from Meta's own phone-numbers reference -
    /// <c>WhatsAppChannelEndpoints</c>' own connect-time validation step, the identical
    /// "prove the token actually works before ever writing a row" reasoning
    /// <c>VkApiClient.GetGroupInfoAsync</c>'s own remarks give, adapted to WhatsApp's own shape: unlike
    /// VK's <c>groups.getById</c> (called with no id, so VK infers the community from the token alone),
    /// WhatsApp's token does not self-disclose which phone number to use - a WhatsApp Business Account can
    /// hold more than one number, and Meta's own API offers no "which number does this token mean by
    /// default" call. So the operator supplies <paramref name="phoneNumberId"/> directly (read off Meta's
    /// own App Dashboard or the Embedded Signup response), and this call's only job is to confirm the
    /// token is actually authorized for that specific number - a real validation, not a discovery, the one
    /// place this item's own design differs from VK's fully-discovered <see cref="WhatsAppPhoneNumberInfo"/> role.
    /// </summary>
    public async Task<WhatsAppPhoneNumberInfo> GetPhoneNumberAsync(
        string token, string phoneNumberId, CancellationToken cancellationToken)
    {
        using var httpRequest = new HttpRequestMessage(HttpMethod.Get, $"{phoneNumberId}?fields=id,display_phone_number,verified_name");
        httpRequest.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);

        using var response = await httpClient.SendAsync(httpRequest, cancellationToken);

        if (response.IsSuccessStatusCode)
        {
            var info = await response.Content.ReadFromJsonAsync<WhatsAppPhoneNumberInfo>(cancellationToken);
            if (info?.Id is { Length: > 0 })
            {
                return info;
            }

            throw new WhatsAppApiCallException("WhatsApp's phone number lookup returned no usable id.");
        }

        var envelope = await response.Content.ReadFromJsonAsync<WhatsAppErrorEnvelope>(cancellationToken);
        var reason = envelope?.Error is { } error
            ? $"WhatsApp rejected the token or phone number id (error {error.Code}): {Truncate(error.Message)}"
            : $"WhatsApp rejected the token or phone number id (HTTP {(int)response.StatusCode}).";
        throw new WhatsAppApiCallException(reason);
    }

    private static string Truncate(string? text) =>
        text is null ? "(no message)" : text.Length > 500 ? text[..500] : text;
}

public sealed record WhatsAppSendResult(bool Success, string? ProviderMessageId, string? RefusalReason)
{
    public static WhatsAppSendResult Sent(string? providerMessageId) => new(true, providerMessageId, null);

    public static WhatsAppSendResult Refused(string reason) => new(false, null, reason);
}
