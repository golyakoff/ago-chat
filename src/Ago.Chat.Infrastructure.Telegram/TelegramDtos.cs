using System.Text.Json.Serialization;

namespace Ago.Chat.Infrastructure.Telegram;

// `14-07`: Telegram's own wire shapes, entirely below the Infrastructure boundary
// (ChannelPortTests.NoProviderVocabulary_AppearsAboveInfrastructure) - nothing here is referenced by
// Ago.Chat.Domain, Ago.Chat.Application or Ago.Chat.Contracts, and TelegramChannelAdapter/
// TelegramInboundMessageParser are the only translators between this vocabulary and the
// channel-neutral one IInboundChannelAdapter defines.
//
// Confirmed against Telegram's own public Bot API documentation (core.telegram.org/bots/api),
// 2026-08-28: every method - success or failure - answers one JSON envelope,
// {"ok": bool, "result": ..., "error_code": int, "description": string}, which is why every response
// below is read through TelegramApiResponse<T> rather than a bare array or object the way MAX's own
// GetUpdatesAsync reads MaxUpdatesEnvelope directly. Every field is nullable and read defensively, the
// same "a wrong or missing field degrades to null, not a crash" discipline MaxDtos.cs states for its
// own honesty note - here backed by the public documentation rather than a best-effort reconstruction,
// since Telegram's Bot API documentation is complete and current.

public sealed record TelegramApiResponse<T>(
    [property: JsonPropertyName("ok")] bool Ok,
    [property: JsonPropertyName("result")] T? Result,
    [property: JsonPropertyName("error_code")] int? ErrorCode,
    [property: JsonPropertyName("description")] string? Description);

public sealed record TelegramUpdate(
    [property: JsonPropertyName("update_id")] long UpdateId,
    [property: JsonPropertyName("message")] TelegramMessage? Message);

public sealed record TelegramMessage(
    [property: JsonPropertyName("message_id")] long? MessageId,
    [property: JsonPropertyName("from")] TelegramUser? From,
    [property: JsonPropertyName("chat")] TelegramChat? Chat,
    [property: JsonPropertyName("text")] string? Text);

public sealed record TelegramUser([property: JsonPropertyName("id")] long? Id);

public sealed record TelegramChat([property: JsonPropertyName("id")] long? Id);

public sealed record TelegramSendMessageRequest(
    [property: JsonPropertyName("chat_id")] long ChatId,
    [property: JsonPropertyName("text")] string Text);
