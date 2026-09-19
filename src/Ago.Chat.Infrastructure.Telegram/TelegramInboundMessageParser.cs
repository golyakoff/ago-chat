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
    /// <summary>
    /// `25-148`: Telegram's own literal wire form for a deep-link tap - opening `t.me/&lt;bot&gt;?start=X`
    /// makes the client send exactly this text, one space, then the payload, as this chat's first
    /// message. Confirmed against Telegram's own Bot API documentation
    /// (core.telegram.org/bots/api#message, the `/start` deep-linking section).
    /// </summary>
    private const string StartCommandPrefix = "/start ";

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

        // `25-164`: Telegram never populates `text` on a photo message - the caption (if any) is a
        // separate `caption` field (core.telegram.org/bots/api#message, confirmed against the public
        // documentation). Falling back to `caption` here, rather than teaching every downstream caller a
        // second "or maybe it's a caption" field, means a captioned photo still produces a plain-text
        // ReceiveChannelMessage alongside its own attachment dispatch below - the identical two-message
        // shape `ParsedMaxMessage`'s own remarks already document for MAX's captioned photo, reached by a
        // different wire route (MAX carries both in one `text` field; Telegram never lets one message
        // carry both, so the two are combined here instead).
        var text = update.Message.Text ?? update.Message.Caption;

        // `25-151`: a Telegram "share contact" message carries no `text` at all - the two are mutually
        // exclusive on Telegram's own wire shape - so this can no longer bail out just because `text` is
        // blank; it must also ask whether a trustworthy contact rode along instead. See
        // TryVerifyContact's own remarks for what "trustworthy" means here.
        var contact = TryVerifyContact(update.Message.Contact, senderId);

        // `25-164`: the identical widening, for a sent photo - confirmed live symptom this closes (a
        // caption-less photo used to vanish entirely, not just lose its attachment) mirrors `25-161`'s
        // own MAX diagnosis exactly; see that item's report for the shared root cause.
        var image = TryExtractImage(update.Message.Photo);

        if (string.IsNullOrWhiteSpace(text) && contact is null && image is null)
        {
            return null;
        }

        if (!string.IsNullOrWhiteSpace(text))
        {
            // `25-148`: strip Telegram's own `/start ` prefix off a deep-link payload before it ever
            // reaches ReceiveChannelMessageHandler - this is the entire mechanism that lets a visitor
            // tapping `AuthEndpoints`' own minted `t.me/<bot>?start=<code>` link confirm a pending
            // channel-identity link (`14-12`/`adr/0079`) exactly the way typing the bare code by hand
            // already does, with no second, parallel verification mechanism: ReceiveChannelMessageHandler's
            // own confirmation branch compares the message body to a live pending code by *exact* equality,
            // never a command parse (that handler's own remarks), so "/start 4821" has to become "4821"
            // here, at the one place this codebase already translates Telegram's own wire vocabulary into a
            // plain message body, or it would never match. A bare "/start" with no payload (a visitor
            // manually starting the bot, not following a link) has no trailing space to strip and is left
            // exactly as it is - it will simply fail to match any pending code, the identical, unremarkable
            // outcome any other non-code text already produces.
            if (text.StartsWith(StartCommandPrefix, StringComparison.Ordinal))
            {
                text = text[StartCommandPrefix.Length..];
            }
        }

        var externalMessageId = $"{chatId}:{messageId}";

        return new ParsedTelegramMessage(chatId, senderId, externalMessageId, text, contact, image);
    }

    /// <summary>
    /// `25-151`: the entire trust mechanism this item's own backlog text names - Telegram attaches no
    /// signature to a shared contact, so <paramref name="senderId"/> (<c>message.from.id</c>) equalling
    /// <see cref="TelegramContact.UserId"/> is the only thing standing between "the sender shared their
    /// own number" and "the sender forwarded someone else's address-book entry." A contact with no phone
    /// number, or one whose <c>user_id</c> is absent or does not match, is rejected here - returned as
    /// <see langword="null"/>, indistinguishable to this method's own caller from "no contact was ever
    /// attached" so the two can be told apart only by looking at <see cref="TelegramMessage.Contact"/>
    /// itself, which is exactly what <see cref="TelegramLongPollingService"/>'s own dispatch does to log a
    /// rejection distinctly from an ordinary absence.
    /// </summary>
    private static ParsedTelegramContact? TryVerifyContact(TelegramContact? contact, long senderId)
    {
        if (contact is null || string.IsNullOrWhiteSpace(contact.PhoneNumber))
        {
            return null;
        }

        if (contact.UserId != senderId)
        {
            return null;
        }

        return new ParsedTelegramContact(contact.PhoneNumber, contact.FirstName, contact.LastName);
    }

    /// <summary>
    /// `25-164`: the inbound half of this item's own diagnosis - before this method existed, nothing in
    /// this file ever read <see cref="TelegramMessage.Photo"/> at all, so a sent photo's own attachment
    /// was invisible to <see cref="TryParse"/> no matter what it carried; a caption-less photo (no
    /// `text`, and now no `caption` either) then failed the blank-body bail-out too, dropping the entire
    /// inbound message - the identical live symptom `25-161`'s own report already found for MAX.
    ///
    /// <para>Picks the highest resolution by <see cref="TelegramPhotoSize.Width"/> explicitly, not by
    /// array position - Telegram's own documentation describes the array as ordered smallest-to-largest
    /// but does not make that an enforced guarantee of the schema itself, so trusting position would be
    /// an assumption this method does not need to make. A size with no <c>file_id</c> at all is not
    /// something Telegram's own documentation describes happening, but is skipped defensively rather than
    /// crashing, the same "an unconfirmed shape degrades to skipped, never guessed at" posture
    /// <see cref="TryVerifyContact"/> already established for its own attachment.</para>
    ///
    /// <para>No trust check the way <see cref="TryVerifyContact"/> needs one: Telegram itself is the one
    /// giving this system the <c>file_id</c>, inside an update this parser's own caller already
    /// authenticated (a long-poll answered against a real bot token - Telegram has no webhook receiver
    /// for this channel at all, <see cref="TelegramBotApiOptions"/>'s own remarks), the identical trust
    /// boundary <see cref="TelegramMessage.Text"/> already crosses with no separate verification of its
    /// own.</para>
    /// </summary>
    private static ParsedTelegramImage? TryExtractImage(IReadOnlyList<TelegramPhotoSize>? photo)
    {
        if (photo is null || photo.Count == 0)
        {
            return null;
        }

        var best = photo
            .Where(size => !string.IsNullOrWhiteSpace(size.FileId))
            .OrderByDescending(size => size.Width ?? 0)
            .FirstOrDefault();

        return best?.FileId is { } fileId ? new ParsedTelegramImage(fileId) : null;
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
/// <summary>
/// `25-151`: <paramref name="Text"/> is nullable since this item - a contact-only message carries none
/// at all (this type's own remarks on <see cref="TelegramInboundMessageParser.TryParse"/>'s new
/// behaviour). Exactly one of <paramref name="Text"/>/<paramref name="Contact"/> is non-null in
/// practice (Telegram's own wire shape makes the two mutually exclusive on one message), but nothing
/// here enforces that as an invariant - the caller (<see cref="TelegramLongPollingService"/>) simply
/// acts on whichever is present.
///
/// <para><b>`25-164`:</b> <paramref name="Image"/> is not mutually exclusive with <paramref name="Text"/>
/// the way <paramref name="Contact"/> is - a captioned photo produces both (<paramref name="Text"/>
/// carrying the caption, via <see cref="TelegramInboundMessageParser.TryParse"/>'s own
/// <c>Text ?? Caption</c> fallback), the same "acts on whichever is present, independently" shape
/// <c>ParsedMaxMessage</c>'s own remarks already document for MAX's captioned photo.</para>
/// </summary>
public sealed record ParsedTelegramMessage(
    long ChatId, long SenderId, string ExternalMessageId, string? Text, ParsedTelegramContact? Contact = null,
    ParsedTelegramImage? Image = null);

/// <summary>
/// `25-151`: a Telegram contact that has already passed <see cref="TelegramInboundMessageParser"/>'s own
/// <c>user_id</c> trust check - by the time this type exists, "is this really the sender's own number" is
/// already answered, so nothing downstream needs to re-ask it.
/// </summary>
public sealed record ParsedTelegramContact(string PhoneNumber, string? FirstName, string? LastName);

/// <summary>
/// `25-164`: a Telegram photo attachment this parser could actually resolve to a <c>file_id</c> - see
/// <see cref="TelegramInboundMessageParser"/>'s own <c>TryExtractImage</c> remarks for why the highest
/// resolution's id is the only field this record carries forward. The caller
/// (<see cref="TelegramLongPollingService"/>) is what actually resolves and downloads the bytes
/// (<see cref="TelegramApiClient.DownloadImageAsync"/>'s own two-step protocol) and hands them to
/// <c>Ago.Chat.Application.UseCases.ReceiveChannelAttachment.ReceiveChannelAttachmentHandler</c> - this
/// type itself carries no bytes and does no I/O, matching <c>ParsedMaxImage</c>'s own shape.
/// </summary>
public sealed record ParsedTelegramImage(string FileId);
