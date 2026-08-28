namespace Ago.Chat.Infrastructure.MaxBot;

/// <summary>
/// `14-02`: the one place a <see cref="MaxUpdate"/> becomes something worth acting on - a pure function,
/// used identically by the webhook receiver (<c>Ago.Chat.Api</c>'s <c>MaxWebhookEndpoints</c>) and the
/// long-polling loop (<see cref="MaxLongPollingService"/>), so the two inbound mechanisms this item ships
/// cannot disagree about what a message is.
///
/// <para>Recognises only <c>update_type == "message_created"</c> - MAX's own envelope carries other
/// event kinds (a bot being started, a chat's title changing) that this item has no use case for; every
/// other kind, and any update whose payload does not match the expected shape, returns
/// <see langword="null"/> rather than throwing, which is what lets a caller "skip the ones we do not
/// understand and keep going" instead of one malformed update stalling either loop.</para>
/// </summary>
public static class MaxInboundMessageParser
{
    private const string MessageCreatedUpdateType = "message_created";

    public static ParsedMaxMessage? TryParse(MaxUpdate update)
    {
        if (update.UpdateType != MessageCreatedUpdateType)
        {
            return null;
        }

        // Found live, 2026-08-28, against a real bot: `sender.user_id` identifies *who wrote it*, not
        // *which conversation to reply into* - MAX's own `POST /messages?chat_id=` refused every
        // outbound reply with `chat.not.found` when this parser handed it the sender's user id instead
        // of `recipient.chat_id`. Both are required for a message this parser accepts: a message with
        // no sender is not a real inbound message, and one with no chat_id has nowhere this system
        // could ever reply to even if it understood everything else about it.
        if (update.Message?.Sender?.UserId is not { } senderId)
        {
            return null;
        }

        if (update.Message.Recipient?.ChatId is not { } chatId)
        {
            return null;
        }

        var text = update.Message.Body?.Text;
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        // The provider's own message id is the idempotency key ExternalMessageId.ToClientMessageId
        // relies on (14-01's own design) - a fallback synthesised from chat+timestamp is used only
        // if MAX ever omits `body.mid`, which nothing in the public documentation confirms it does or
        // does not do. Recorded here rather than assumed away: a real captured payload is what should
        // remove this fallback or confirm it is dead code.
        var externalMessageId = update.Message.Body?.Mid
            ?? $"{chatId}:{update.Message.Timestamp ?? update.Timestamp ?? 0}";

        return new ParsedMaxMessage(chatId, senderId, externalMessageId, text);
    }
}

/// <summary>
/// <paramref name="ChatId"/> is what this system stores as the <c>ChannelIdentity</c>'s own external
/// address and what every outbound reply is sent to - MAX's own `POST /messages?chat_id=` operates on
/// chats, not users, and a 1:1 bot conversation's chat id is a value MAX assigns, not the same number
/// as the sender's own <paramref name="SenderId"/> (found live, 2026-08-28 - see
/// <see cref="MaxInboundMessageParser.TryParse"/>'s own remarks). <paramref name="SenderId"/> is kept on
/// this record for whichever future caller needs to know who specifically wrote a message inside a
/// chat (nothing does yet); it is deliberately not part of the channel identity <see cref="ChatId"/>
/// alone already resolves.
/// </summary>
public sealed record ParsedMaxMessage(long ChatId, long SenderId, string ExternalMessageId, string Text);
