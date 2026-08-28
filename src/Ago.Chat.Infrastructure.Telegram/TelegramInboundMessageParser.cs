namespace Ago.Chat.Infrastructure.Telegram;

/// <summary>
/// `14-07`: the one place a <see cref="TelegramUpdate"/> becomes something worth acting on - a pure
/// function, used by <see cref="TelegramLongPollingService"/> (this channel's only caller; unlike MAX
/// there is no webhook receiver to share it with - see <see cref="TelegramBotApiOptions"/>'s own
/// remarks).
///
/// <para>Recognises only an update that carries a <c>message</c> - Telegram's own envelope carries other
/// update kinds this item has no use case for (<c>edited_message</c>, <c>callback_query</c>, a chat
/// member changing, ...), none of which this parser's DTO even declares a field for, so they simply
/// deserialize to a <see cref="TelegramUpdate"/> whose <see cref="TelegramUpdate.Message"/> is
/// <see langword="null"/> and are skipped the same way an unrecognised MAX <c>update_type</c> is -
/// "skip the ones we do not understand and keep going" rather than one malformed update stalling the
/// poll loop.</para>
///
/// <para><b>Why the external message id is always <c>chat_id:message_id</c>, never <c>message_id</c>
/// alone - the one place this item's parser cannot copy MAX's shape.</b> MAX's own parser uses the
/// provider's <c>mid</c> directly, falling back to a chat+timestamp composite only if <c>mid</c> is
/// absent, because MAX's <c>mid</c> is (as far as this item's own sources could confirm) globally
/// unique. Telegram's <c>message_id</c> is explicitly documented as unique only <em>within one chat</em>
/// - two different chats can and will produce the same <c>message_id</c>. <see cref="ExternalMessageId"/>'s
/// idempotency hash already mixes in <see cref="ChannelKind"/> to stop two different <em>channels</em>
/// from colliding, but nothing stops two different Telegram <em>chats</em> from colliding on a bare
/// <c>message_id</c> - and a collision here is not a cosmetic bug, it is silent message loss: the second
/// chat's message would be treated as a redelivery of the first chat's message and dropped
/// (<see cref="Ago.Chat.Domain.ExternalMessageId"/>'s own remarks on what a colliding id costs). So the
/// composite is this channel's only strategy, not a fallback for a rare missing field the way it is for
/// MAX.</para>
/// </summary>
public static class TelegramInboundMessageParser
{
    public static ParsedTelegramMessage? TryParse(TelegramUpdate update)
    {
        if (update.Message is null)
        {
            return null;
        }

        if (update.Message.From?.Id is not { } senderId)
        {
            return null;
        }

        if (update.Message.Chat?.Id is not { } chatId)
        {
            return null;
        }

        if (update.Message.MessageId is not { } messageId)
        {
            return null;
        }

        var text = update.Message.Text;
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var externalMessageId = $"{chatId}:{messageId}";

        return new ParsedTelegramMessage(chatId, senderId, externalMessageId, text);
    }
}

/// <summary>
/// <paramref name="ChatId"/> is what this system stores as the <c>ChannelIdentity</c>'s own external
/// address and what every outbound reply is sent to - Telegram's own <c>sendMessage</c> operates on
/// chats, not users, and for a group chat the sender's own <paramref name="SenderId"/> is a different
/// number entirely (for a 1:1 bot conversation the two happen to coincide, but nothing in this parser
/// relies on that). <paramref name="SenderId"/> is kept on this record for whichever future caller needs
/// to know who specifically wrote a message inside a chat (nothing does yet); it is deliberately not
/// part of the channel identity <see cref="ChatId"/> alone already resolves - the same split
/// <c>ParsedMaxMessage</c> draws.
/// </summary>
public sealed record ParsedTelegramMessage(long ChatId, long SenderId, string ExternalMessageId, string Text);
