using System.Text.Json.Serialization;

namespace Ago.Chat.Infrastructure.Fcm;

/// <summary>
/// `26-100`: FCM HTTP v1's own send-API request shape - `{"message": {...}}`, `message.token`,
/// `message.data`, `message.android`. Deliberately no <c>notification</c> object and no top-level display
/// fields: `adr/0179` §3's client-owns-loudness design (carried forward by `adr/0181`) only works for a
/// message the OS does not render itself, so the app's own receiver builds the notification from
/// <see cref="FcmMessage.Data"/>. This is the identical data-only shape
/// <c>Ago.Chat.Infrastructure.RuStore.RuStoreSendRequest</c> produces, so the Android client reads the
/// same keys regardless of which transport delivered the message.
/// </summary>
internal sealed record FcmSendRequest(
    [property: JsonPropertyName("message")] FcmMessage Message);

/// <summary>
/// <see cref="Data"/> is the whole payload - see <see cref="FcmPushSender.BuildData"/> for how
/// <c>PushMessage.Title</c>/<c>Body</c>/<c>GroupKey</c> and its own <c>Data</c> map are folded into this
/// one flat <c>map[string]string</c>, exactly as the RuStore adapter does, so the two transports are
/// wire-compatible from the client's point of view.
///
/// <para><see cref="Android"/> is never <see langword="null"/>: it carries the ttl and, critically, the
/// high delivery priority a data-only message needs to wake the app promptly on a Doze-ing device - see
/// <see cref="FcmAndroidConfig"/>'s own remarks. There is deliberately no <c>notification</c> property on
/// this type at all: adding one a future caller might populate "just this once" would be the exact
/// regression `adr/0179` §3's whole design depends on never happening, so the shape itself makes it
/// impossible rather than merely discouraged - the identical guard the RuStore message type carries.</para>
/// </summary>
internal sealed record FcmMessage(
    [property: JsonPropertyName("token")] string Token,
    [property: JsonPropertyName("data")] IReadOnlyDictionary<string, string> Data,
    [property: JsonPropertyName("android")] FcmAndroidConfig Android);

/// <summary>
/// `26-100`: FCM v1's <c>AndroidConfig</c>, narrowed to the two fields this design sets.
///
/// <para><see cref="Ttl"/> is a <c>google.protobuf.Duration</c> - a decimal number of seconds suffixed
/// with a literal <c>"s"</c> (e.g. <c>"300s"</c>) - the same format
/// <c>Ago.Chat.Infrastructure.RuStore.RuStoreAndroidConfig.Ttl</c> already sends (RuStore borrowed it
/// from FCM); here it is the field's real, documented home rather than a borrowed guess.</para>
///
/// <para><see cref="Priority"/> is <c>"high"</c>, and this is a delivery decision, not a loudness one - the
/// distinction `adr/0179` §3 turns on. FCM defers <em>normal</em>-priority data-only messages while a
/// device is in Doze, which is precisely the late-delivery `26-100` exists to remove; <c>"high"</c> asks
/// FCM to deliver immediately and wake the app. It does not make the notification loud or even visible -
/// there is still no <c>notification</c> block, so the client alone decides whether and how to alert
/// (`adr/0179` §3 / `adr/0181`). Setting transport priority and owning notification loudness are
/// orthogonal, and only the former belongs on the wire.</para>
/// </summary>
internal sealed record FcmAndroidConfig(
    [property: JsonPropertyName("ttl")] string Ttl,
    [property: JsonPropertyName("priority")] string Priority);

/// <summary>
/// FCM v1's own error body - <c>{"error": {"code", "message", "status", "details": [...]}}</c>. The
/// per-message, revocation-relevant code is not the top-level canonical <see cref="FcmError.Status"/> but
/// the <c>FcmError.errorCode</c> inside <see cref="FcmError.Details"/> (e.g. <c>UNREGISTERED</c>), so
/// <see cref="FcmPushSender"/> keys on that first and falls back to <see cref="FcmError.Status"/> and then
/// the numeric HTTP status - never on <see cref="FcmError.Message"/>, which is free-form prose.
/// </summary>
internal sealed record FcmErrorEnvelope(
    [property: JsonPropertyName("error")] FcmError? Error);

internal sealed record FcmError(
    [property: JsonPropertyName("code")] int? Code,
    [property: JsonPropertyName("message")] string? Message,
    [property: JsonPropertyName("status")] string? Status,
    [property: JsonPropertyName("details")] IReadOnlyList<FcmErrorDetail>? Details);

internal sealed record FcmErrorDetail(
    [property: JsonPropertyName("@type")] string? Type,
    [property: JsonPropertyName("errorCode")] string? ErrorCode);

/// <summary>
/// The subset of a Google service-account key file this adapter reads. Parsed once from
/// <see cref="FcmOptions.ServiceAccountJson"/> by <see cref="FcmServiceAccountTokenProvider"/> to build
/// and sign the OAuth2 JWT assertion. <see cref="TokenUri"/> is nullable so a real key file's own value
/// is honoured while a test's minimal fixture can omit it and fall back to Google's documented default.
/// </summary>
internal sealed record FcmServiceAccount(
    [property: JsonPropertyName("client_email")] string? ClientEmail,
    [property: JsonPropertyName("private_key")] string? PrivateKey,
    [property: JsonPropertyName("token_uri")] string? TokenUri);

/// <summary>Google's OAuth2 token-endpoint success body - only the two fields the mint needs.</summary>
internal sealed record FcmTokenResponse(
    [property: JsonPropertyName("access_token")] string? AccessToken,
    [property: JsonPropertyName("expires_in")] int ExpiresIn);
