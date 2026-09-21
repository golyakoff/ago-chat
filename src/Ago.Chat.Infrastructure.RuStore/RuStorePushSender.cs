using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Ago.Chat.Application.Abstractions;

namespace Ago.Chat.Infrastructure.RuStore;

/// <summary>
/// `26-04`/`adr/0180`: the one class in this codebase that speaks RuStore's own Push send-API shape -
/// deliberately thin, no retry, no timeout, no circuit breaker
/// (<c>Ago.Chat.Module.Push.ResilientPushSender</c>, wrapped around this class in
/// <c>Ago.Chat.Worker</c>'s own composition root, is where all four of those live), the identical
/// division <c>MaxApiClient</c>/<c>VkApiClient</c>/<c>WhatsAppApiClient</c> already establish and
/// <see cref="IPushSender"/>'s own remarks require. <c>HttpClient.BaseAddress</c> (including the project
/// id path segment) and the <c>Authorization: Bearer</c> header are both set once, at the composition
/// root, the same "the client class stays thin" split <c>YooKassaPaymentsApiClient</c>'s own remarks
/// describe for its Basic-auth header - this class never reads <see cref="RuStoreOptions"/> itself.
///
/// <para><b>Terminal/transient, restated for RuStore's own five documented outcomes.</b> Every one of
/// `400`/`401`/`403`/`404`/`429`/`500` is a real, parseable RuStore-issued JSON body -
/// <c>26-04</c>'s own backlog item found this live, against a garbage bearer token, and the finding
/// (a genuine `401 UNAUTHORIZED` body, not a network failure) is exactly what makes this class treat
/// every one of the five as a *value*, never a thrown exception: <see cref="IPushSender.SendAsync"/>'s
/// own remarks state the general rule this specialises. Only a response this class cannot parse into
/// that shape at all - a broken connection, a timeout, an edge/CDN rejection with no application-level
/// body, or an HTTP status/`status` combination none of the five documented outcomes name - is thrown,
/// so the wrapping resilience pipeline's retry and circuit breaker treat it as what it actually is: the
/// network or the provider itself misbehaving, not a definitive answer.</para>
/// </summary>
public sealed class RuStorePushSender(HttpClient httpClient) : IPushSender
{
    public async Task<PushSendOutcome> SendAsync(PushMessage message, CancellationToken cancellationToken)
    {
        var request = new RuStoreSendRequest(new RuStoreMessage(
            message.DeviceToken,
            BuildData(message),
            new RuStoreAndroidConfig(FormatTtl(message.TimeToLive))));

        // The leading "./" is load-bearing, not decoration: .NET's Uri combining rules treat a bare
        // "messages:send" as an *absolute* URI with scheme "messages" (RuStore's own REST action-suffix
        // convention happens to look exactly like a URI scheme separator to RFC 3986's parser), which
        // silently discards HttpClient.BaseAddress entirely rather than throwing - confirmed against a
        // real System.Uri combine before this item trusted it. "./messages:send" combines against the
        // base address the way every other relative path in this codebase already does.
        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, "./messages:send")
        {
            Content = JsonContent.Create(request),
        };

        using var response = await httpClient.SendAsync(httpRequest, cancellationToken);

        if (response.IsSuccessStatusCode)
        {
            return new PushSendOutcome.Delivered();
        }

        RuStoreErrorEnvelope? envelope;
        try
        {
            envelope = await response.Content.ReadFromJsonAsync<RuStoreErrorEnvelope>(cancellationToken);
        }
        catch (JsonException)
        {
            // A body that is not JSON at all - an edge/CDN error page (e.g. a proxy's own HTML "502 Bad
            // Gateway"), not RuStore's own application layer. Folded into the same "no parseable error
            // body" outcome below rather than letting a raw JsonException escape, so every non-2xx
            // response this class cannot read as a documented RuStore refusal throws the identical,
            // clearly-labelled exception type.
            envelope = null;
        }

        if (envelope is null || (envelope.Status is not { Length: > 0 } && envelope.Code is null))
        {
            // No parseable RuStore error body at all - `26-04`'s own live reachability probe never saw
            // this (every real response it drew, even for a deliberately garbage token, carried a real
            // `x-vkpns-request-id`-bearing body), so this is genuinely the "something between us and
            // RuStore's own application layer failed" case, not a documented refusal.
            throw new HttpRequestException(
                $"RuStore returned HTTP {(int)response.StatusCode} for POST messages:send with no parseable error body.",
                null, response.StatusCode);
        }

        return Classify(response.StatusCode, envelope);
    }

    /// <summary>
    /// Resolves `adr/0180` §3's own named ambiguity - RuStore's published validation rule reads "if
    /// <c>message.data.payload</c> is present and non-empty" while <c>message.data</c> is typed in the
    /// same document as a flat <c>map[string]string</c>. **This item's own best-guess reading, not a
    /// verified one**: a flat map carrying whatever keys the payload actually needs, with no nested
    /// <c>payload</c> wrapper key - the more standard shape for this kind of API, and the one
    /// `push-notifications.md`'s own examples already describe the payload as. No live RuStore project
    /// or service token exists in this deployment to send a real message against and observe which
    /// reading RuStore's own validator actually wants (this item's own report says so plainly);
    /// <see cref="RuStorePushSenderTests"/> pins down exactly what JSON this method currently produces,
    /// so the day a real send is possible, correcting this (if correction turns out to be needed) is a
    /// one-method change with a test already in place to update.
    ///
    /// <para><see cref="PushMessage.Title"/>/<see cref="PushMessage.Body"/>/<see cref="PushMessage.GroupKey"/>
    /// are folded into this same flat map, under <c>title</c>/<c>body</c>/<c>groupKey</c>, and take
    /// precedence over anything already present under those keys in <see cref="PushMessage.Data"/> -
    /// RuStore's send API has no <c>notification</c> object populated (`adr/0179` §3's data-only design)
    /// and no <c>collapse_key</c> field (`adr/0180` §4a), so this is the only place either the display
    /// text or the client-side grouping value can travel at all.</para>
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

    /// <summary>See <see cref="RuStoreAndroidConfig.Ttl"/>'s own remarks for why this is a
    /// whole-seconds, <c>"Ns"</c>-suffixed string rather than a bare number - an assumption borrowed from
    /// FCM v1's identically-named field, not a RuStore-confirmed shape.</summary>
    private static string FormatTtl(TimeSpan ttl) => $"{(long)ttl.TotalSeconds}s";

    /// <summary>
    /// `adr/0180` §9's own table, plus the live-observed `401` `26-04`'s backlog item records: keys on
    /// <see cref="RuStoreErrorEnvelope.Status"/> first (RuStore's own enumerated field), falling back to
    /// the numeric HTTP status only when <c>status</c> itself is missing or not one of the five this
    /// item was written against - never on <see cref="RuStoreErrorEnvelope.Message"/>, for the reason
    /// that type's own remarks give.
    /// </summary>
    private static PushSendOutcome Classify(HttpStatusCode statusCode, RuStoreErrorEnvelope envelope)
    {
        var reason = $"RuStore {envelope.Status ?? statusCode.ToString()} "
            + $"({envelope.Code ?? (int)statusCode}): {envelope.Message ?? "(no message)"}";

        return envelope.Status switch
        {
            // Terminal - this device's own registration is dead. `26-05`'s own handler revokes the row.
            "INVALID_ARGUMENT" => new PushSendOutcome.TokenGone(reason),
            "NOT_FOUND" => new PushSendOutcome.TokenGone(reason),
            // `adr/0180` §9's own honest gap: named in the `status` field's example values, absent from
            // the enumerated error list - treated as terminal if it ever arrives, without depending on it.
            "UNREGISTERED" => new PushSendOutcome.TokenGone(reason),

            // Never a device fault - both are *our own credential's* problem
            // (`26-04`'s own backlog item's Done-when: getting this backwards would empty the whole
            // table the first time the service token was ever wrong).
            "PERMISSION_DENIED" => new PushSendOutcome.TransientFailure(reason),
            "UNAUTHORIZED" => new PushSendOutcome.TransientFailure(reason),

            // RuStore's own outage or overload - nothing about any one device.
            "TOO_MANY_REQUESTS" => new PushSendOutcome.TransientFailure(reason),
            "INTERNAL" => new PushSendOutcome.TransientFailure(reason),

            _ => ClassifyByHttpStatus(statusCode, reason),
        };
    }

    /// <summary>The fallback for a response whose `status` field is missing or unrecognised but whose
    /// HTTP status code still matches one of the five documented outcomes - defensive, since RuStore's
    /// own documentation states the HTTP status always matches `code` and this item has no evidence that
    /// `status` is ever absent from a real response. Anything genuinely unrecognised on both axes throws,
    /// the identical "err toward retrying an unrecognised shape rather than silently misclassifying it"
    /// default <c>MaxApiClient</c>/<c>VkApiClient</c>/<c>WhatsAppApiClient</c> each already apply to their
    /// own provider's undocumented codes.</summary>
    private static PushSendOutcome ClassifyByHttpStatus(HttpStatusCode statusCode, string reason) => statusCode switch
    {
        HttpStatusCode.BadRequest => new PushSendOutcome.TokenGone(reason),
        HttpStatusCode.NotFound => new PushSendOutcome.TokenGone(reason),
        HttpStatusCode.Unauthorized => new PushSendOutcome.TransientFailure(reason),
        HttpStatusCode.Forbidden => new PushSendOutcome.TransientFailure(reason),
        HttpStatusCode.TooManyRequests => new PushSendOutcome.TransientFailure(reason),
        HttpStatusCode.InternalServerError => new PushSendOutcome.TransientFailure(reason),
        _ => throw new HttpRequestException(
            $"RuStore returned an unrecognised outcome for POST messages:send: {reason}", null, statusCode),
    };
}
