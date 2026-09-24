using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Ago.Chat.Application.Abstractions;

namespace Ago.Chat.Infrastructure.Fcm;

/// <summary>
/// `26-100`/`adr/0181`: the one class in this codebase that speaks FCM HTTP v1's own send-API shape - the
/// primary operator-push transport, RuStore the fallback. Deliberately thin, no retry, no timeout, no
/// circuit breaker (<c>Ago.Chat.Module.Push.ResilientPushSender</c>, wrapped around this class in
/// <c>Ago.Chat.Worker</c>'s own composition root, is where all four of those live), the identical
/// division <see cref="Ago.Chat.Application.Abstractions.IPushSender"/>'s own remarks require and the
/// RuStore adapter already follows.
///
/// <para><b>The bearer is per-request, unlike RuStore's.</b> RuStore presents one long-lived token set
/// once on the <see cref="System.Net.Http.HttpClient"/> at the composition root; FCM's access token is a
/// short-lived OAuth2 token minted and cached by <see cref="IFcmAccessTokenProvider"/>, so it is fetched
/// per send and set on the <see cref="HttpRequestMessage"/> rather than on the shared client (which would
/// not be thread-safe to mutate anyway). <c>HttpClient.BaseAddress</c> - host plus the
/// `/v1/projects/{projectId}/` path - is still set once at the root, the same "the client class stays
/// thin" split.</para>
///
/// <para><b>Terminal/transient, mapped to the same three-way <see cref="PushSendOutcome"/> RuStore uses.</b>
/// FCM answers a failed send with a real, parseable JSON error body carrying a canonical `status` and,
/// inside `details`, an `FcmError.errorCode`. Per `adr/0181` and this item's brief, only
/// <c>UNREGISTERED</c> and <c>NOT_FOUND</c> mean <em>this token is dead</em> and map to
/// <see cref="PushSendOutcome.TokenGone"/>; every other definitive answer - including
/// <c>INVALID_ARGUMENT</c> and the auth failures that are our own credential's fault - maps to
/// <see cref="PushSendOutcome.TransientFailure"/>, never a revocation, for the identical reason the RuStore
/// adapter never lets a credential fault empty the device table. This is a deliberate narrowing versus the
/// RuStore adapter, which also treats <c>INVALID_ARGUMENT</c> as token-gone: for FCM the token-death
/// signal is specifically <c>UNREGISTERED</c>, and a 400 can equally be a malformed request of ours, so
/// erring toward "keep the row, retry" is the safe default. Only a response this class cannot parse into a
/// definitive FCM answer at all - a broken connection, a timeout, an edge/CDN body - is thrown, so the
/// wrapping resilience pipeline retries what genuinely might get a different answer.</para>
/// </summary>
public sealed class FcmPushSender(HttpClient httpClient, IFcmAccessTokenProvider tokenProvider) : IPushSender
{
    public async Task<PushSendOutcome> SendAsync(PushMessage message, CancellationToken cancellationToken)
    {
        var accessToken = await tokenProvider.GetAccessTokenAsync(cancellationToken);

        var request = new FcmSendRequest(new FcmMessage(
            message.DeviceToken,
            BuildData(message),
            new FcmAndroidConfig(FormatTtl(message.TimeToLive), "high")));

        // The leading "./" is load-bearing, exactly as it is for RuStore: .NET's Uri combining rules treat
        // a bare "messages:send" as an absolute URI with scheme "messages" (the REST action-suffix colon
        // looks like a scheme separator to RFC 3986's parser), silently discarding HttpClient.BaseAddress.
        // "./messages:send" combines against the base address the way every other relative path does.
        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, "./messages:send")
        {
            Content = JsonContent.Create(request),
        };
        httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        using var response = await httpClient.SendAsync(httpRequest, cancellationToken);

        if (response.IsSuccessStatusCode)
        {
            return new PushSendOutcome.Delivered();
        }

        FcmErrorEnvelope? envelope;
        try
        {
            envelope = await response.Content.ReadFromJsonAsync<FcmErrorEnvelope>(cancellationToken);
        }
        catch (JsonException)
        {
            // A body that is not JSON at all - an edge/CDN error page, not FCM's own application layer.
            // Folded into the same "no parseable error body" throw below rather than letting a raw
            // JsonException escape.
            envelope = null;
        }

        if (envelope?.Error is not { } error
            || (error.Status is not { Length: > 0 } && FirstErrorCode(error) is null))
        {
            throw new HttpRequestException(
                $"FCM returned HTTP {(int)response.StatusCode} for POST messages:send with no parseable error body.",
                null, response.StatusCode);
        }

        return Classify(response.StatusCode, error);
    }

    /// <summary>
    /// Folds <see cref="PushMessage.Title"/>/<see cref="PushMessage.Body"/>/<see cref="PushMessage.GroupKey"/>
    /// into the flat <c>data</c> map alongside whatever <see cref="PushMessage.Data"/> already carries -
    /// byte-for-byte the same construction
    /// <c>Ago.Chat.Infrastructure.RuStore.RuStorePushSender.BuildData</c> performs, so a device receives an
    /// identical payload whichever transport delivered it. FCM's send API has no <c>notification</c> object
    /// populated (`adr/0179` §3's data-only design) and no collapse-key field, so this flat map is the only
    /// place either the display text or the client-side grouping value travels.
    /// </summary>
    private static IReadOnlyDictionary<string, string> BuildData(PushMessage message)
    {
        var data = new Dictionary<string, string>(message.Data, StringComparer.Ordinal)
        {
            ["title"] = message.Title,
            ["body"] = message.Body,
            ["groupKey"] = message.GroupKey,
        };
        return data;
    }

    /// <summary>A <c>google.protobuf.Duration</c> - a whole number of seconds suffixed with a literal
    /// <c>"s"</c> - FCM v1's documented `android.ttl` format, identical to what the RuStore adapter sends.</summary>
    private static string FormatTtl(TimeSpan ttl) => $"{(long)ttl.TotalSeconds}s";

    private static string? FirstErrorCode(FcmError error) =>
        error.Details?.Select(detail => detail.ErrorCode).FirstOrDefault(code => code is { Length: > 0 });

    /// <summary>
    /// Keys on FCM's per-message <c>FcmError.errorCode</c> first (the value inside `details`), then the
    /// canonical `status`, then the numeric HTTP status - never on the free-form `message`. Only
    /// <c>UNREGISTERED</c>/<c>NOT_FOUND</c> are token-death; everything else definitive is transient.
    /// </summary>
    private static PushSendOutcome Classify(HttpStatusCode statusCode, FcmError error)
    {
        var errorCode = FirstErrorCode(error);
        var reason = $"FCM {errorCode ?? error.Status ?? statusCode.ToString()} "
            + $"({error.Code ?? (int)statusCode}): {error.Message ?? "(no message)"}";

        // The device's own registration is dead - the caller revokes the row.
        if (errorCode is "UNREGISTERED" or "NOT_FOUND" || error.Status is "NOT_FOUND")
        {
            return new PushSendOutcome.TokenGone(reason);
        }

        // Everything else FCM answered definitively: INVALID_ARGUMENT (possibly our malformed request),
        // SENDER_ID_MISMATCH (a token for a different project - a config fault, not a dead token),
        // UNAUTHENTICATED/PERMISSION_DENIED/THIRD_PARTY_AUTH_ERROR (our credential), QUOTA_EXCEEDED,
        // UNAVAILABLE, INTERNAL (FCM's own overload/outage). None is a statement that this one device's
        // registration died, so none revokes it - the caller records the failure and retries as before.
        if (errorCode is not null || error.Status is not null)
        {
            return new PushSendOutcome.TransientFailure(reason);
        }

        return ClassifyByHttpStatus(statusCode, reason);
    }

    /// <summary>Fallback when neither an `errorCode` nor a canonical `status` was present but the HTTP
    /// status still matches a known FCM outcome. A 404 with no body-level code is still token-death; the
    /// rest are transient; anything genuinely unrecognised throws, so the resilience pipeline gets a
    /// chance at a real answer rather than this adapter guessing one - the identical default the RuStore
    /// adapter applies.</summary>
    private static PushSendOutcome ClassifyByHttpStatus(HttpStatusCode statusCode, string reason) => statusCode switch
    {
        HttpStatusCode.NotFound => new PushSendOutcome.TokenGone(reason),
        HttpStatusCode.BadRequest => new PushSendOutcome.TransientFailure(reason),
        HttpStatusCode.Unauthorized => new PushSendOutcome.TransientFailure(reason),
        HttpStatusCode.Forbidden => new PushSendOutcome.TransientFailure(reason),
        HttpStatusCode.TooManyRequests => new PushSendOutcome.TransientFailure(reason),
        HttpStatusCode.InternalServerError => new PushSendOutcome.TransientFailure(reason),
        HttpStatusCode.ServiceUnavailable => new PushSendOutcome.TransientFailure(reason),
        _ => throw new HttpRequestException(
            $"FCM returned an unrecognised outcome for POST messages:send: {reason}", null, statusCode),
    };
}
