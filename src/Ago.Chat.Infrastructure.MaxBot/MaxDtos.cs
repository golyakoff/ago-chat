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
    [property: JsonPropertyName("text")] string? Text);

public sealed record MaxSendMessageRequest([property: JsonPropertyName("text")] string Text);

public sealed record MaxSendMessageResponse([property: JsonPropertyName("message")] MaxSentMessage? Message);

public sealed record MaxSentMessage([property: JsonPropertyName("body")] MaxMessageBody? Body);

public sealed record MaxSubscribeRequest(
    [property: JsonPropertyName("url")] string Url,
    [property: JsonPropertyName("secret")] string Secret,
    [property: JsonPropertyName("update_types")] IReadOnlyList<string> UpdateTypes);
