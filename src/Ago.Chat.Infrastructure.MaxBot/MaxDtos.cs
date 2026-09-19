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
/// `25-151`/`25-161`: one entry of <see cref="MaxMessageBody.Attachments"/> - shares this file's own
/// top-level honesty note in full: MAX's public documentation describes an attachment only in outline,
/// so this shape (and <see cref="MaxAttachmentPayload"/>/<see cref="MaxContactInfo"/> beneath it) is
/// this item's own best-effort reconstruction from public third-party write-ups, not a confirmed capture
/// against a live bot - flagged again, explicitly, in this item's own report rather than left to be
/// rediscovered the way this file's own top-level note already warns a "wrong guess" should be.
/// </summary>
public sealed record MaxAttachment(
    [property: JsonPropertyName("type")] string? Type,
    [property: JsonPropertyName("payload")] MaxAttachmentPayload? Payload);

/// <summary>
/// `25-151`/`25-161`: one attachment's payload, whatever <see cref="MaxAttachment.Type"/> says it is -
/// named for the property, not for either kind it carries, because <c>System.Text.Json</c> binds one
/// property to one CLR type and MAX's own wire shape puts every attachment kind's fields on the same
/// <c>payload</c> object. This was <c>MaxContactAttachmentPayload</c> before `25-161` added
/// <see cref="PhotoId"/>/<see cref="Token"/>/<see cref="Url"/> beside the original contact fields - a
/// rename, not a new type, because a type called "the contact payload" that also carries a photo's own
/// fields would actively mislead the next reader. Only the fields matching the actual attachment's own
/// kind ever populate; the rest simply deserialize to <see langword="null"/>, the identical "read
/// defensively, degrade rather than throw" discipline this file's own top-level note already states for
/// every field here.
///
/// <para><see cref="Hash"/> is MAX's substitute for Telegram's `user_id` equality check
/// (<c>TelegramContact</c>'s own remarks) for a <c>"contact"</c> attachment: an HMAC-SHA256 of
/// <see cref="VcfInfo"/>, keyed with the receiving site's own bot token - see
/// <c>MaxInboundMessageParser.TryVerifyContact</c>'s own remarks for the verification itself, and this
/// item's own report for why the exact digest encoding (hex vs base64) is an assumption a real capture
/// must settle.</para>
///
/// <para><b>`25-161`: <see cref="PhotoId"/>/<see cref="Token"/>/<see cref="Url"/>, for an
/// <c>"image"</c> attachment.</b> Confirmed (unlike this file's own contact-payload guesswork above)
/// against the MAX Bot API's own published Go client schema
/// (<c>github.com/max-messenger/max-bot-api-client-go/schemes</c>, <c>PhotoAttachmentPayload</c>):
/// <c>photo_id</c>/<c>token</c> are MAX-internal references usable only when re-sending a photo through
/// MAX's own API, and <c>url</c> is, in MAX's own words, "a direct link to image in internet" - the one
/// field this codebase can actually fetch bytes from without a second, undocumented MAX endpoint.
/// <see cref="MaxInboundMessageParser"/> reads only <see cref="Url"/> for exactly that reason; an
/// inbound photo whose payload has no <c>url</c> (token-only) cannot be downloaded by anything this
/// class currently has - the same "an unconfirmed shape degrades to skipped, not guessed at" posture the
/// contact half already established.</para>
/// </summary>
public sealed record MaxAttachmentPayload(
    [property: JsonPropertyName("vcf_info")] string? VcfInfo,
    [property: JsonPropertyName("max_info")] MaxContactInfo? MaxInfo,
    [property: JsonPropertyName("hash")] string? Hash,
    [property: JsonPropertyName("photo_id")] long? PhotoId = null,
    [property: JsonPropertyName("token")] string? Token = null,
    [property: JsonPropertyName("url")] string? Url = null);

/// <summary>
/// `25-151`: the structured half of a shared-contact attachment - alongside <see
/// cref="MaxAttachmentPayload.VcfInfo"/>'s own vCard-formatted string, this is this item's own
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

public sealed record MaxSendMessageRequest(
    [property: JsonPropertyName("text")] string Text,
    // `25-152`: WhenWritingNull, not the default "always write" - an ordinary reply (every channel this
    // item does not touch, and MAX itself before this item) must serialize byte-identically to what this
    // client has always sent, and a bare `null` attachments key is a field MAX's own /messages endpoint
    // never saw from this codebase before today.
    [property: JsonPropertyName("attachments")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    IReadOnlyList<MaxOutboundAttachment>? Attachments = null);

/// <summary>
/// `25-152`: one entry of <see cref="MaxSendMessageRequest.Attachments"/> - the outbound mirror of
/// <see cref="MaxAttachment"/>, built rather than parsed. Shares this file's own top-level honesty note
/// in full and adds to it: MAX's public documentation (dev.max.ru/docs-api) names <c>inline_keyboard</c>
/// as one of the attachment types a message may carry and separately documents its own button
/// vocabulary (<c>callback</c>, <c>link</c>, <c>request_contact</c>, <c>request_geo_location</c>,
/// <c>open_app</c>, <c>message</c>, <c>clipboard</c>), but the exact envelope one wraps the other in -
/// this record's own <see cref="Type"/>/<see cref="Payload"/> split - is this item's own best-effort
/// reconstruction from the same public third-party integration write-ups and client-library source
/// `MaxAttachment`'s own remarks already lean on for the inbound shape, not a confirmed request/response
/// capture against a live bot; the standing caveat `25-151` names for its own inbound reconstruction
/// applies identically here, and repeated in this item's own report rather than left to be
/// rediscovered. <see cref="MaxChannelAdapter"/>'s own Done-when is explicit that this shape is
/// verifiable only against MAX's documented outline, not against a real MAX bot, until a token exists.
/// </summary>
public sealed record MaxOutboundAttachment(
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("payload")] MaxInlineKeyboardPayload Payload);

/// <summary>`25-152`: an <c>inline_keyboard</c> attachment's own payload - rows of buttons, the same
/// "array of arrays" shape Telegram's own <c>ReplyKeyboardMarkup.Keyboard</c> uses for the unrelated
/// reply-keyboard case, and MAX's own documented "at most three <c>request_contact</c>-type buttons per
/// row" constraint is a caller-side discipline (<see cref="MaxChannelAdapter"/> never builds more than
/// one), not something this record itself enforces - the same "do not validate what the caller already
/// controls" restraint <see cref="MessagePayload"/>'s own remarks describe for a different boundary.
/// </summary>
public sealed record MaxInlineKeyboardPayload(
    [property: JsonPropertyName("buttons")] IReadOnlyList<IReadOnlyList<MaxInlineKeyboardButton>> Buttons);

/// <summary>`25-152`: one button of a <see cref="MaxInlineKeyboardPayload"/> row. <see cref="Type"/> is
/// MAX's own closed button vocabulary (this file's own remarks list all seven members); only
/// <c>"request_contact"</c> has a caller in this codebase - "do not model what nothing reads" applies to
/// the other six exactly as it does to <c>MaxGetMeResponse</c>'s own unused fields.</summary>
public sealed record MaxInlineKeyboardButton(
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("text")] string Text);

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
