using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;

namespace Ago.Chat.Infrastructure.Avito;

/// <summary>
/// `14-11`: the one class in this codebase that speaks Avito's own Messenger API HTTP shape -
/// deliberately thin, no retry, no timeout, no circuit breaker (<c>ChannelKind.Avito</c>'s adapter,
/// wrapped in <c>Ago.Chat.Module.Channels.ResilientInboundChannelAdapter</c>, is where all four of those
/// live for <see cref="SendMessageAsync"/>), the identical division <c>MaxApiClient</c>/<c>VkApiClient</c>/
/// <c>WhatsAppApiClient</c> already establish.
///
/// <para><b>Terminal/transient split, and the honest gap in it.</b> Every other channel's own client in
/// this codebase found a real, confirmed numeric error-code table for message-send refusals (MAX's
/// status-code table, VK's <c>error_code</c> table, WhatsApp's <c>error.code</c> table). This item's own
/// research found no equivalent for Avito - the OpenAPI schema used as this item's source documents only
/// the generic <c>authError</c> (401)/<c>forbiddenError</c> (403) shapes shared across Avito's entire API
/// surface, with no messenger-specific refusal taxonomy anywhere in it. So this class classifies by HTTP
/// status alone, the same coarse-grained shape <c>MaxApiClient</c> uses (confirmed real non-200 statuses
/// on failure, per this item's own research): <c>403</c> (forbidden - wrong scope, or blocked from this
/// chat), <c>404</c> (the chat or account does not exist - deleted, or a stale <c>chat_id</c>) and
/// <c>422</c> (the request itself is malformed - e.g. an over-length message) are client-shaped, permanent
/// refusals; everything else (429 rate limiting, 5xx, a dropped connection) defaults to transient, the
/// same "err toward retrying, not toward silently giving up on a real outage" default every precedent in
/// this stage applies to an unclassified code. <c>401</c> is deliberately <em>not</em> in the terminal
/// set - see <see cref="AvitoAccessTokenExpiredException"/>'s own remarks for why it means something
/// different for this specific provider.</para>
/// </summary>
public sealed class AvitoApiClient(HttpClient httpClient)
{
    private static readonly HashSet<HttpStatusCode> TerminalRefusalStatusCodes =
        [HttpStatusCode.Forbidden, HttpStatusCode.NotFound, HttpStatusCode.UnprocessableEntity];

    /// <summary>
    /// <c>GET /core/v1/accounts/self</c> - <c>AvitoChannelEndpoints</c>' own connect-time validation and
    /// discovery step, the identical "prove the token actually works, and learn the value every send
    /// needs, before ever writing a row" reasoning <c>VkApiClient.GetGroupInfoAsync</c>/
    /// <c>WhatsAppApiClient.GetPhoneNumberAsync</c> already establish.
    /// </summary>
    public async Task<AvitoUserInfoSelf> GetSelfAsync(string accessToken, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "core/v1/accounts/self");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        using var response = await httpClient.SendAsync(request, cancellationToken);

        if (response.IsSuccessStatusCode)
        {
            var info = await response.Content.ReadFromJsonAsync<AvitoUserInfoSelf>(cancellationToken);
            if (info is { Id: > 0 })
            {
                return info;
            }

            throw new AvitoApiCallException("Avito's own accounts/self returned no usable id.");
        }

        throw new AvitoApiCallException(await DescribeFailureAsync(response, cancellationToken));
    }

    /// <summary>
    /// <c>POST /messenger/v3/webhook</c> - registers the callback URL <em>this specific credential's own
    /// token</em> should deliver to. Called with a per-credential URL
    /// (<c>https://&lt;public-base&gt;/webhooks/avito/{credentialId}?secret=...</c>) rather than one fixed
    /// App-wide URL - <c>AvitoWebhookEndpoints</c>' own remarks explain why Avito's own API, unlike
    /// WhatsApp's Meta App Dashboard configuration, makes this both possible and the lower-risk choice.
    /// </summary>
    public async Task SubscribeWebhookAsync(string accessToken, Uri callbackUrl, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "messenger/v3/webhook")
        {
            Content = JsonContent.Create(new AvitoWebhookSubscribeRequest(callbackUrl.ToString())),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        using var response = await httpClient.SendAsync(request, cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            throw new AvitoApiCallException(await DescribeFailureAsync(response, cancellationToken));
        }
    }

    /// <summary>
    /// <c>POST /messenger/v1/accounts/{user_id}/chats/{chat_id}/messages</c> - the outbound send. Throws
    /// <see cref="AvitoAccessTokenExpiredException"/> specifically on 401 rather than folding it into the
    /// terminal-refusal set - see that type's own remarks.
    /// </summary>
    public async Task<AvitoSendResult> SendMessageAsync(
        string accessToken, long userId, string chatId, string text, CancellationToken cancellationToken)
    {
        var body = new AvitoSendMessageRequest(AvitoMessageTypes.Text, new AvitoSendMessageBody(text));

        using var request = new HttpRequestMessage(HttpMethod.Post, $"messenger/v1/accounts/{userId}/chats/{chatId}/messages")
        {
            Content = JsonContent.Create(body),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        using var response = await httpClient.SendAsync(request, cancellationToken);

        if (response.IsSuccessStatusCode)
        {
            var sent = await response.Content.ReadFromJsonAsync<AvitoSendMessageResponse>(cancellationToken);
            return AvitoSendResult.Sent(sent?.Id);
        }

        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            throw new AvitoAccessTokenExpiredException(await DescribeFailureAsync(response, cancellationToken));
        }

        if (TerminalRefusalStatusCodes.Contains(response.StatusCode))
        {
            return AvitoSendResult.Refused(await DescribeFailureAsync(response, cancellationToken));
        }

        throw new HttpRequestException(
            $"Avito API returned HTTP {(int)response.StatusCode} for the messages endpoint: "
            + await DescribeFailureAsync(response, cancellationToken));
    }

    /// <summary>
    /// <c>POST /token</c> with <c>grant_type=refresh_token</c> - form-encoded, the one call in this class
    /// that is not JSON and not <c>Authorization: Bearer</c> (Avito's own client-identity fields travel in
    /// the body instead, confirmed from the schema's own <c>RefreshRequest</c> definition). Called by
    /// <see cref="AvitoChannelAdapter"/> reactively, after a 401, never proactively - see
    /// <see cref="Domain.ChannelCredential.RotateOAuthTokens"/>'s own remarks.
    /// </summary>
    public async Task<AvitoRefreshTokenResponse> RefreshAccessTokenAsync(
        string clientId, string clientSecret, string refreshToken, CancellationToken cancellationToken)
    {
        var form = new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["client_id"] = clientId,
            ["client_secret"] = clientSecret,
            ["refresh_token"] = refreshToken,
        };

        using var response = await httpClient.PostAsync("token", new FormUrlEncodedContent(form), cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            throw new AvitoApiCallException(await DescribeFailureAsync(response, cancellationToken));
        }

        var refreshed = await response.Content.ReadFromJsonAsync<AvitoRefreshTokenResponse>(cancellationToken);
        if (refreshed is { AccessToken: { Length: > 0 }, RefreshToken: { Length: > 0 } })
        {
            return refreshed;
        }

        throw new AvitoApiCallException("Avito's own /token refresh returned no usable access_token/refresh_token pair.");
    }

    private static async Task<string> DescribeFailureAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        AvitoErrorEnvelope? envelope;
        try
        {
            envelope = await response.Content.ReadFromJsonAsync<AvitoErrorEnvelope>(cancellationToken);
        }
        catch (System.Text.Json.JsonException)
        {
            envelope = null;
        }

        return envelope?.Error is { } error
            ? $"Avito rejected the request (HTTP {(int)response.StatusCode}, error {error.Code}): {Truncate(error.Message)}"
            : $"Avito rejected the request (HTTP {(int)response.StatusCode}) with no parseable error body.";
    }

    private static string Truncate(string? text) =>
        text is null ? "(no message)" : text.Length > 500 ? text[..500] : text;
}

public sealed record AvitoSendResult(bool Success, string? ProviderMessageId, string? RefusalReason)
{
    public static AvitoSendResult Sent(string? providerMessageId) => new(true, providerMessageId, null);

    public static AvitoSendResult Refused(string reason) => new(false, null, reason);
}
