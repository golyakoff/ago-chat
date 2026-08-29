using System.Text.Json.Serialization;

namespace Ago.Chat.Infrastructure.Vk;

// `14-08`: VK's own wire shapes, entirely below the Infrastructure boundary
// (ChannelPortTests.NoProviderVocabulary_AppearsAboveInfrastructure) - nothing here is referenced by
// Ago.Chat.Domain, Ago.Chat.Application or Ago.Chat.Contracts, and VkChannelAdapter/
// VkInboundMessageParser/VkWebhookEndpoints are the only translators between this vocabulary and the
// channel-neutral one IInboundChannelAdapter defines.
//
// **Honesty note, the same discipline MaxDtos.cs states for itself, but on firmer ground here**: no VK
// community access token was available while this item was built, so nothing below was captured from a
// real request or response. Unlike MAX's reconstruction (built from third-party write-ups because
// dev.vk.com itself was unreachable from this environment), these shapes were read directly out of
// VK's own official open-source SDK (github.com/VKCOM/vk-php-sdk, fetched 2026-08-29: the Callback API
// server-handler base classes for the confirmation/message_new envelope field names, and the
// `Messages`/`Groups` action classes' own PHPDoc parameter lists for the outbound calls) - source code
// VK itself publishes and maintains, generated from VK's own API JSON Schema
// (github.com/VKCOM/vk-api-schema), not a guess and not a third party's own guess. What is not
// confirmed from that source is called out field by field below. VkInboundMessageParserTests documents
// exactly which shape was assumed either way, and is the first thing to fix against a real captured
// payload once a token exists.

/// <summary>
/// One Callback API delivery, VK's own outer envelope for every event this system's webhook receives -
/// confirmed field names and nesting from <c>VKCallbackApiServerHandler::parse</c> and its base class'
/// own event-key constants (<c>type</c>, <c>object</c>, <c>secret</c>, <c>group_id</c>). <c>Object</c>
/// is left as a raw <see cref="System.Text.Json.JsonElement"/> because its shape depends entirely on
/// <c>Type</c> - VK defines dozens of event types this item has no use case for, and a strongly typed
/// field here would force every one of them to parse before this system could even read <c>Type</c> to
/// decide whether to bother.
/// </summary>
public sealed record VkCallbackEvent(
    [property: JsonPropertyName("type")] string? Type,
    [property: JsonPropertyName("group_id")] long? GroupId,
    [property: JsonPropertyName("secret")] string? Secret,
    [property: JsonPropertyName("event_id")] string? EventId,
    [property: JsonPropertyName("object")] System.Text.Json.JsonElement? Object);

/// <summary>
/// <see cref="VkCallbackEvent.Type"/>'s own <c>"confirmation"</c> value - VK's own event-type constant,
/// confirmed from the same source as <see cref="VkCallbackEvent"/>.
/// </summary>
public static class VkCallbackEventTypes
{
    public const string Confirmation = "confirmation";
    public const string MessageNew = "message_new";
}

/// <summary>
/// <see cref="VkCallbackEvent.Object"/>'s own shape when <see cref="VkCallbackEvent.Type"/> is
/// <c>"message_new"</c> - <c>{ message, client_info }</c>, confirmed against a real captured payload
/// published in a third-party write-up (this item could not reach dev.vk.com directly to confirm this
/// specific nesting from VK's own documentation prose, unlike the envelope-level field names above,
/// which came straight from VK's own SDK source). <c>client_info</c> is deliberately not modelled -
/// nothing here has a use for it.
/// </summary>
public sealed record VkMessageNewObject([property: JsonPropertyName("message")] VkMessage? Message);

/// <summary>
/// One VK message, as it appears inside a <c>message_new</c> event's own <c>object.message</c>.
///
/// <para><see cref="VkMessage.Out"/> is the field this item's own design leans on hardest, and it has no
/// equivalent in MAX's or Telegram's own inbound shape: VK's Callback API fires <c>message_new</c> for
/// a community's <em>outgoing</em> messages too (<c>out == 1</c>), not only messages a visitor sent to
/// the community (<c>out == 0</c>) - see <see cref="VkInboundMessageParser"/>'s own remarks for why an
/// adapter that ignored this would create a reply loop the very first time an operator answered through
/// this channel.</para>
/// </summary>
public sealed record VkMessage(
    [property: JsonPropertyName("id")] long? Id,
    [property: JsonPropertyName("date")] long? Date,
    [property: JsonPropertyName("from_id")] long? FromId,
    [property: JsonPropertyName("peer_id")] long? PeerId,
    [property: JsonPropertyName("text")] string? Text,
    [property: JsonPropertyName("out")] int? Out);

// --- Outbound: messages.send, groups.getById, groups.getCallbackConfirmationCode ---
//
// Confirmed from VKCOM/vk-php-sdk's own VKApiRequest.php and VKApiClient.php, 2026-08-29: base URL
// `https://api.vk.com/method`, every call is a POST of `application/x-www-form-urlencoded` params
// (access_token, v, plus the method's own params) to `{base}/{method}`, and - the one genuinely
// unusual thing about this provider relative to MAX/Telegram - **every response is HTTP 200**,
// success or failure. VK's own REST convention puts the outcome in the JSON body: `{"response": ...}`
// on success, `{"error": {"error_code": N, "error_msg": "..."}}` on failure - confirmed from
// VKApiRequest::parseResponse, which never branches on the HTTP status code at all, only on which of
// those two JSON keys is present. VkApiClient reads the body regardless of status for this reason;
// treating a non-200 the way MaxApiClient does would silently accept every VK failure as a success.

public sealed record VkApiError(
    [property: JsonPropertyName("error_code")] int ErrorCode,
    [property: JsonPropertyName("error_msg")] string? ErrorMsg);

/// <summary><c>messages.send</c>'s own success shape is a bare integer (the new message's own id), not
/// an object - confirmed from VKApiRequest::parseResponse, which returns `decode_body['response']`
/// unwrapped for every method alike.</summary>
public sealed record VkSendMessageEnvelope(
    [property: JsonPropertyName("response")] long? Response,
    [property: JsonPropertyName("error")] VkApiError? Error);

/// <summary><c>groups.getById</c>'s own success shape, called with no <c>group_id</c> parameter at all
/// so VK resolves it from the calling token's own identity - confirmed as a legitimate call shape from
/// VK\Actions\Groups::getById accepting an empty <c>params</c> array. The <c>groups</c> array wrapper
/// (rather than a bare array of group objects) is this item's own best-effort assumption, carried over
/// from this same SDK's general "wrap collection responses in a named array" convention seen elsewhere
/// in this schema-generated client (e.g. <c>messages.getConversations</c>'s own <c>items</c> wrapper) -
/// not confirmed against a real response, and the first thing to fix once a token exists.</summary>
public sealed record VkGroupsGetByIdEnvelope(
    [property: JsonPropertyName("response")] VkGroupsGetByIdResult? Response,
    [property: JsonPropertyName("error")] VkApiError? Error);

public sealed record VkGroupsGetByIdResult([property: JsonPropertyName("groups")] IReadOnlyList<VkGroup>? Groups);

public sealed record VkGroup(
    [property: JsonPropertyName("id")] long Id,
    [property: JsonPropertyName("name")] string? Name);

/// <summary><c>groups.getCallbackConfirmationCode</c>'s own success shape - confirmed to exist, with a
/// required <c>group_id</c> parameter, from VK\Actions\Groups::getCallbackConfirmationCode's own PHPDoc;
/// the response's own field name (<c>code</c>) is this item's own best-effort assumption from the
/// method's name and purpose, not confirmed against a real response.</summary>
public sealed record VkGetCallbackConfirmationCodeEnvelope(
    [property: JsonPropertyName("response")] VkConfirmationCodeResult? Response,
    [property: JsonPropertyName("error")] VkApiError? Error);

public sealed record VkConfirmationCodeResult([property: JsonPropertyName("code")] string? Code);
