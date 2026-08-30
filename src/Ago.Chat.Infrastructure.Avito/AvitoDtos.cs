using System.Text.Json.Serialization;

namespace Ago.Chat.Infrastructure.Avito;

// `14-11`: Avito's own wire shapes, entirely below the Infrastructure boundary
// (ChannelPortTests.NoProviderVocabulary_AppearsAboveInfrastructure) - nothing here is referenced by
// Ago.Chat.Domain, Ago.Chat.Application or Ago.Chat.Contracts, and AvitoChannelAdapter/
// AvitoInboundMessageParser/AvitoWebhookEndpoints are the only translators between this vocabulary and
// the channel-neutral one IInboundChannelAdapter defines.
//
// **Honesty note, the same discipline VkDtos.cs/WhatsAppDtos.cs each state for themselves.**
// developers.avito.ru itself was not reachable from this environment (WebFetch and a direct curl both
// failed/redirected without returning content) - the same "official docs unreachable" situation `14-02`'s
// MAX and `14-08`'s VK each report. This item's source of truth is Avito's own published OpenAPI 3.0
// specification, mirrored verbatim at github.com/MissiaL/avito-api (references/avito-api-openapi.json,
// fetched 2026-08-30) - a machine-readable schema Avito itself authors and publishes for its own API
// catalog (developers.avito.ru/api-catalog), not a third party's write-up or guess, closer in kind to
// `14-08`'s VK SDK-source citation than to `14-02`'s MAX reconstruction. Every field name below is taken
// directly from that schema's own `WebhookMessage`/`sendMessageRequestBody`/`UserInfoSelf`/`RefreshRequest`
// definitions. What is NOT confirmed against a real delivery is whether Avito's real webhook traffic
// matches this schema exactly (the same gap every channel adapter in this stage names for itself) - and
// one further gap unique to this provider: the inbound webhook signature header
// (`x-avito-messenger-signature`) has no documented algorithm anywhere in this schema, and a public
// developer discussion (qna.habr.com/q/1404944, fetched 2026-08-30) shows Avito's own support unable to
// answer what algorithm produces it, over a month after being asked. This item does not attempt to verify
// that header - AvitoWebhookEndpoints' own remarks explain the different authentication mechanism built
// instead.

/// <summary>
/// One webhook delivery's outer envelope - confirmed field names from Avito's own OpenAPI schema (the
/// `201` response example on `POST /messenger/v3/webhook`, which doubles as the delivery payload shape).
/// Unlike WhatsApp's natively-batched envelope, Avito delivers exactly one event per HTTP call - closer to
/// MAX's/VK's own single-event shape.
/// </summary>
public sealed record AvitoWebhookEnvelope(
    [property: JsonPropertyName("id")] string? Id,
    [property: JsonPropertyName("version")] string? Version,
    [property: JsonPropertyName("timestamp")] long? Timestamp,
    [property: JsonPropertyName("payload")] AvitoWebhookPayload? Payload);

/// <summary><see cref="Type"/> is always the literal string <c>"message"</c> for every payload this item
/// has a use for - confirmed as the schema's own example value. Kept as a plain string rather than a
/// closed enum because nothing here branches on it beyond what <see cref="AvitoInboundMessageParser"/>
/// already does by reading <see cref="Value"/> directly - the identical "do not model what nothing reads"
/// restraint <c>WhatsAppWebhookEnvelope.Object</c>'s own remarks apply to WhatsApp's own top-level
/// discriminator.</summary>
public sealed record AvitoWebhookPayload(
    [property: JsonPropertyName("type")] string? Type,
    [property: JsonPropertyName("value")] AvitoWebhookMessage? Value);

/// <summary>
/// One Avito Messenger message, exactly as it appears in a webhook delivery's own <c>payload.value</c> -
/// confirmed field names and types from the schema's own <c>WebhookMessage</c> definition.
///
/// <para><see cref="ChatType"/> is the field this item's own "listing-scoped" investigation turned on:
/// <c>u2i</c> (a chat about a listing/объявление), <c>u2u</c> (a chat about the seller's profile, no
/// listing involved) or <c>a2u</c> (a chat with Avito itself) - confirmed as the schema's own enum. <see
/// cref="ItemId"/> is populated only for <c>u2i</c> chats (the schema's own description: "актуально
/// только для чатов с типом u2i"). Neither field is read by <see cref="AvitoInboundMessageParser"/> or
/// stored anywhere in this system - <see cref="ChatId"/> is what this item uses instead, and it already
/// encodes the listing distinction implicitly (Avito mints a distinct <see cref="ChatId"/> per listing a
/// buyer contacts a seller about) without AGO Chat ever learning the word "listing" - see
/// <see cref="AvitoInboundMessageParser"/>'s own remarks and this item's own report for the full
/// reasoning and the concrete scenario that ruled out <see cref="AuthorId"/> as the address
/// instead.</para>
///
/// <para><see cref="UserId"/> is what this item resolves an inbound delivery's tenant by - the schema's
/// own description states plainly: "Это всегда аккаунт, на который зарегистрирован вебхук" ("this is
/// always the account the webhook is registered to"). Unlike WhatsApp, where this same kind of
/// self-identifying field is load-bearing because the webhook URL itself carries no per-tenant
/// information, this item's own webhook is registered per-credential (a
/// <c>{credentialId}</c> URL path segment, matching MAX's/VK's own shape - <c>AvitoWebhookEndpoints</c>'s
/// own remarks on why Avito did not need WhatsApp's answer), so <see cref="UserId"/> is read only as a
/// sanity check against the credential the path segment already named, never as the primary routing
/// key.</para>
/// </summary>
public sealed record AvitoWebhookMessage(
    [property: JsonPropertyName("id")] string? Id,
    [property: JsonPropertyName("chat_id")] string? ChatId,
    [property: JsonPropertyName("chat_type")] string? ChatType,
    [property: JsonPropertyName("author_id")] long? AuthorId,
    [property: JsonPropertyName("user_id")] long? UserId,
    [property: JsonPropertyName("item_id")] long? ItemId,
    [property: JsonPropertyName("type")] string? Type,
    [property: JsonPropertyName("content")] AvitoMessageContent? Content,
    [property: JsonPropertyName("created")] long? Created);

/// <summary>Only <see cref="Text"/> is modelled - confirmed present on the schema's own
/// <c>MessageContent</c> definition, alongside a dozen other type-specific fields (image, voice, call,
/// location, link, item, flow_id for a bot) this item has no use case for, the identical
/// "recognise the one shape this item handles, skip the rest" restraint <c>WhatsAppMessage.Type</c>'s own
/// remarks state for WhatsApp's own message-type breadth.</summary>
public sealed record AvitoMessageContent([property: JsonPropertyName("text")] string? Text);

/// <summary><c>chat_type</c>'s own three values - confirmed from the schema's own enum, kept as constants
/// rather than a C# enum because nothing here needs a closed type for it (<see cref="AvitoInboundMessageParser"/>
/// never switches on it - see this file's own remarks on why).</summary>
public static class AvitoChatTypes
{
    public const string Item = "u2i";
    public const string User = "u2u";
    public const string Avito = "a2u";
}

/// <summary><c>content.type</c>'s own value for a free-form text message - the only one this item's
/// inbound parser accepts.</summary>
public static class AvitoMessageTypes
{
    public const string Text = "text";
}

// --- Outbound: POST /messenger/v3/webhook (subscribe) ---

public sealed record AvitoWebhookSubscribeRequest([property: JsonPropertyName("url")] string Url);

public sealed record AvitoWebhookSubscribeResponse([property: JsonPropertyName("ok")] bool Ok);

// --- Outbound: GET /core/v1/accounts/self ---

/// <summary>Confirmed from the schema's own <c>UserInfoSelf</c> definition. <see cref="Id"/> is what
/// this item stores as <see cref="Domain.ChannelCredential.ProviderAccountId"/> - the seller's own
/// numeric Avito user id, needed on every outbound send (<c>POST
/// /messenger/v1/accounts/{user_id}/chats/{chat_id}/messages</c>) - the identical
/// "discovered once at connect time, reused on every send" shape
/// <c>VkApiClient.GetGroupInfoAsync</c>/<c>WhatsAppApiClient.GetPhoneNumberAsync</c> already
/// establish.</summary>
public sealed record AvitoUserInfoSelf([property: JsonPropertyName("id")] long Id);

// --- Outbound: POST /messenger/v1/accounts/{user_id}/chats/{chat_id}/messages ---
//
// Confirmed from the schema's own `sendMessageRequestBody`/success-response definitions. The schema's
// own `required: ["url"]` on this request body is a documented inconsistency this item does not carry
// forward - "url" is not even a property of this object, evidently a copy-paste artifact elsewhere in
// Avito's own schema, not a real requirement of this endpoint. What this item treats as actually
// required is `message.text`, per the schema's own description and example.

public sealed record AvitoSendMessageRequest(
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("message")] AvitoSendMessageBody Message);

public sealed record AvitoSendMessageBody([property: JsonPropertyName("text")] string Text);

/// <summary>The schema's own success shape for a sent message - <see cref="Id"/> is this item's own
/// <c>ProviderMessageId</c>. The schema's own <c>x-examples</c> shows <c>"direction": "in"</c> on what is
/// unambiguously an outbound-send example (evidently a second copy-paste artifact in Avito's own
/// documentation), so <see cref="Direction"/> is modelled but never read by this item - nothing here is
/// load-bearing on its value.</summary>
public sealed record AvitoSendMessageResponse(
    [property: JsonPropertyName("id")] string? Id,
    [property: JsonPropertyName("direction")] string? Direction,
    [property: JsonPropertyName("type")] string? Type,
    [property: JsonPropertyName("created")] long? Created);

/// <summary>Avito's own generic error envelope, confirmed from the schema's own <c>authError</c>/
/// <c>forbiddenError</c>/<c>serviceError</c> definitions, which all share this identical
/// <c>{"error":{"code":N,"message":"..."}}</c> shape.</summary>
public sealed record AvitoErrorEnvelope([property: JsonPropertyName("error")] AvitoApiError? Error);

public sealed record AvitoApiError(
    [property: JsonPropertyName("code")] int Code,
    [property: JsonPropertyName("message")] string? Message);

// --- OAuth: POST /token (grant_type=refresh_token) ---
//
// Confirmed from the schema's own `RefreshRequest`/refresh-response definitions - form-encoded
// (application/x-www-form-urlencoded), not JSON, the one call in this file that is not.

public sealed record AvitoRefreshTokenResponse(
    [property: JsonPropertyName("access_token")] string? AccessToken,
    [property: JsonPropertyName("refresh_token")] string? RefreshToken,
    [property: JsonPropertyName("expires_in")] int? ExpiresIn,
    [property: JsonPropertyName("token_type")] string? TokenType,
    [property: JsonPropertyName("scope")] string? Scope);
