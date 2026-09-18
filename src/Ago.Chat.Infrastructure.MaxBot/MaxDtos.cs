using System.Text.Json.Serialization;

namespace Ago.Chat.Infrastructure.MaxBot;

// `14-02`: MAX's own wire shapes, entirely below the Infrastructure boundary
// (ChannelPortTests.NoProviderVocabulary_AppearsAboveInfrastructure) - nothing here is referenced by
// Ago.Chat.Domain, Ago.Chat.Application or Ago.Chat.Contracts, and MaxChannelAdapter/
// MaxInboundMessageParser are the only translators between this vocabulary and the channel-neutral one
// IInboundChannelAdapter defines.
//
// **Honesty note, repeated in this item's own report**: MAX's public documentation
// (dev.max.ru/docs-api) describes the update envelope only in outline (`update_type`, `timestamp`, an
// event-specific payload) and the field names below are this item's best-effort reconstruction from
// public third-party integration write-ups and client-library source, not a confirmed response capture
// against a live bot - no token was available while this item was built. Every field is read
// defensively (nullable, tolerant of an unexpected shape) so a wrong guess degrades to "this update was
// not understood" rather than a crash; MaxInboundMessageParserTests documents exactly which shape was
// assumed, and is the first thing to fix against a real captured payload once a token exists.

public sealed record MaxUpdate(
    [property: JsonPropertyName("update_type")] string? UpdateType,
    [property: JsonPropertyName("timestamp")] long? Timestamp,
    [property: JsonPropertyName("message")] MaxIncomingMessage? Message);

public sealed record MaxUpdatesEnvelope(
    [property: JsonPropertyName("updates")] IReadOnlyList<MaxUpdate>? Updates,
    [property: JsonPropertyName("marker")] long? Marker);

public sealed record MaxIncomingMessage(
    [property: JsonPropertyName("sender")] MaxUser? Sender,
    [property: JsonPropertyName("recipient")] MaxRecipient? Recipient,
    [property: JsonPropertyName("body")] MaxMessageBody? Body,
    [property: JsonPropertyName("timestamp")] long? Timestamp);

public sealed record MaxUser([property: JsonPropertyName("user_id")] long? UserId);

public sealed record MaxRecipient([property: JsonPropertyName("chat_id")] long? ChatId);

public sealed record MaxMessageBody(
    [property: JsonPropertyName("mid")] string? Mid,
    [property: JsonPropertyName("text")] string? Text,
    [property: JsonPropertyName("attachments")] IReadOnlyList<MaxAttachment>? Attachments = null);

/// <summary>
/// `25-151`: one entry of <see cref="MaxMessageBody.Attachments"/> - shares this file's own top-level
/// honesty note in full: MAX's public documentation describes an attachment only in outline, so this
/// shape (and <see cref="MaxContactAttachmentPayload"/>/<see cref="MaxContactInfo"/> beneath it) is this
/// item's own best-effort reconstruction from public third-party write-ups, not a confirmed capture
/// against a live bot - flagged again, explicitly, in this item's own report rather than left to be
/// rediscovered the way this file's own top-level note already warns a "wrong guess" should be.
/// </summary>
public sealed record MaxAttachment(
    [property: JsonPropertyName("type")] string? Type,
    [property: JsonPropertyName("payload")] MaxContactAttachmentPayload? Payload);

/// <summary>
/// `25-151`: MAX's own "shared contact" attachment payload, present only when the visitor shared their
/// own contact via a `request_contact`-type button or shared it unprompted - the same payload shape
/// either way (`25-152`'s own outbound half is the only thing that differs, not this shape). <see
/// cref="Hash"/> is MAX's substitute for Telegram's `user_id` equality check
/// (<c>TelegramContact</c>'s own remarks): an HMAC-SHA256 of <see cref="VcfInfo"/>, keyed with the
/// receiving site's own bot token - see <c>MaxInboundMessageParser.TryVerifyContact</c>'s own remarks for
/// the verification itself, and this item's own report for why the exact digest encoding (hex vs
/// base64) is an assumption a real capture must settle.
/// </summary>
public sealed record MaxContactAttachmentPayload(
    [property: JsonPropertyName("vcf_info")] string? VcfInfo,
    [property: JsonPropertyName("max_info")] MaxContactInfo? MaxInfo,
    [property: JsonPropertyName("hash")] string? Hash);

/// <summary>
/// `25-151`: the structured half of a shared-contact attachment - alongside <see
/// cref="MaxContactAttachmentPayload.VcfInfo"/>'s own vCard-formatted string, this is this item's own
/// assumption that MAX also echoes the same facts as plain fields (the same "documentation is only an
/// outline" honesty note this file's own top level note already states), since parsing a vCard blob just
/// to re-extract what a structured sibling field would already give directly is exactly the kind of
/// guesswork a real captured payload should replace, not compound.
/// </summary>
public sealed record MaxContactInfo(
    [property: JsonPropertyName("user_id")] long? UserId,
    [property: JsonPropertyName("phone")] string? Phone,
    [property: JsonPropertyName("first_name")] string? FirstName,
    [property: JsonPropertyName("last_name")] string? LastName);

public sealed record MaxSendMessageRequest([property: JsonPropertyName("text")] string Text);

public sealed record MaxSendMessageResponse([property: JsonPropertyName("message")] MaxSentMessage? Message);

public sealed record MaxSentMessage([property: JsonPropertyName("body")] MaxMessageBody? Body);

public sealed record MaxSubscribeRequest(
    [property: JsonPropertyName("url")] string Url,
    [property: JsonPropertyName("secret")] string Secret,
    [property: JsonPropertyName("update_types")] IReadOnlyList<string> UpdateTypes);

/// <summary>
/// `25-147`: <c>GET /me</c>'s own success shape - MAX's Bot API documentation (dev.max.ru/docs-api)
/// describes the full response as <c>{user_id, first_name, username, is_bot, description, avatar_url,
/// commands}</c>; only <see cref="Username"/> has a caller (<see cref="MaxApiClient.GetMeAsync"/>'s own
/// remarks on why the rest is not modelled - "do not model what nothing reads",
/// <c>VkCallbackEvent.Type</c>'s own restraint applied here).
/// </summary>
public sealed record MaxGetMeResponse([property: JsonPropertyName("username")] string? Username);
