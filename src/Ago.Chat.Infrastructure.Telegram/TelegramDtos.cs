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
    [property: JsonPropertyName("text")] string? Text,
    [property: JsonPropertyName("contact")] TelegramContact? Contact = null);

/// <summary>
/// `25-151`: Telegram's own "share a contact" message shape (core.telegram.org/bots/api#contact),
/// confirmed against the public Bot API documentation - unlike <c>MaxDtos.cs</c>'s own contact shape,
/// this one carries no honesty caveat, the same "documentation is complete and current" standing this
/// file's own top-level remarks already give Telegram's wire shapes in general.
///
/// <para><see cref="UserId"/> is what <see cref="TelegramInboundMessageParser"/>'s own trust check
/// exists for: present only when the shared contact is a Telegram user, and the mandatory discriminator
/// between "the sender shared their own number" (<c>UserId == message.from.id</c>) and "the sender
/// forwarded someone else's address-book entry" - Telegram signs nothing here, so this equality is the
/// entire trust mechanism (this item's own backlog text).</para>
/// </summary>
public sealed record TelegramContact(
    [property: JsonPropertyName("phone_number")] string? PhoneNumber,
    [property: JsonPropertyName("first_name")] string? FirstName,
    [property: JsonPropertyName("last_name")] string? LastName,
    [property: JsonPropertyName("user_id")] long? UserId);

/// <summary>
/// `25-147`: <see cref="Username"/> joins as an additive field - unused by every existing caller
/// (<see cref="TelegramMessage.From"/>, which has never read anything off this type but <see cref="Id"/>),
/// and read for the first time by <see cref="TelegramApiClient.GetMeAsync"/>'s own success path, since
/// Telegram's `getMe` response is this identical `User` shape (`core.telegram.org/bots/api#user`).
/// </summary>
public sealed record TelegramUser(
    [property: JsonPropertyName("id")] long? Id,
    [property: JsonPropertyName("username")] string? Username = null);

public sealed record TelegramChat([property: JsonPropertyName("id")] long? Id);

public sealed record TelegramSendMessageRequest(
    [property: JsonPropertyName("chat_id")] long ChatId,
    [property: JsonPropertyName("text")] string Text,
    // `25-152`: WhenWritingNull, not the default "always write" - an ordinary reply (every channel this
    // item does not touch, and Telegram itself before this item) must serialize byte-identically to what
    // this client has always sent, and a bare `null` reply_markup key is a field Telegram's own
    // sendMessage never saw from this codebase before today.
    [property: JsonPropertyName("reply_markup")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    TelegramReplyKeyboardMarkup? ReplyMarkup = null);

/// <summary>
/// `25-152`: Telegram's own reply-keyboard shape (core.telegram.org/bots/api#replykeyboardmarkup) - a
/// keyboard, not the inline buttons `sendMessage` never uses today, confirmed against the public Bot API
/// documentation the same standing this file's own top-level remarks already give Telegram's wire shapes
/// in general (no honesty caveat needed, unlike MAX's own reconstruction below <c>MaxDtos.cs</c>).
/// <see cref="ResizeKeyboard"/>/<see cref="OneTimeKeyboard"/> are both set <see langword="true"/> by
/// <see cref="TelegramChannelAdapter"/> - a full-size custom keyboard left open after the one tap it
/// exists for would outlive its own usefulness and crowd out the visitor's own text input.
/// </summary>
public sealed record TelegramReplyKeyboardMarkup(
    [property: JsonPropertyName("keyboard")] IReadOnlyList<IReadOnlyList<TelegramKeyboardButton>> Keyboard,
    [property: JsonPropertyName("resize_keyboard")] bool ResizeKeyboard,
    [property: JsonPropertyName("one_time_keyboard")] bool OneTimeKeyboard);

/// <summary>`25-152`: one button of a <see cref="TelegramReplyKeyboardMarkup"/> row -
/// <see cref="RequestContact"/> is the one field this item needs; Telegram's own documentation lists
/// several sibling request types (<c>request_location</c>, <c>request_poll</c>, a Web App button) this
/// codebase has no caller for and does not model ("do not model what nothing reads", the same restraint
/// <c>MaxGetMeResponse</c>'s own remarks state).</summary>
public sealed record TelegramKeyboardButton(
    [property: JsonPropertyName("text")] string Text,
    [property: JsonPropertyName("request_contact")] bool RequestContact = false);
