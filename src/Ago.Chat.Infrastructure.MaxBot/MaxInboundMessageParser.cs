using System.Security.Cryptography;
using System.Text;

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

    /// <summary>`25-151`: MAX's own best-effort-reconstructed attachment type name for a shared contact
    /// - <c>MaxContactAttachmentPayload</c>'s own remarks carry the full honesty note.</summary>
    private const string ContactAttachmentType = "contact";

    /// <summary>
    /// `25-151`: <paramref name="botToken"/> joins this method's own signature - still a pure function
    /// (deterministic given its inputs, no I/O of its own), just one whose trust decision for a MAX
    /// contact attachment genuinely needs a secret neither <see cref="MaxUpdate"/> nor this method carries
    /// on its own. Defaults to an empty string so every call site that only ever handles ordinary text
    /// (and every existing test) keeps compiling unchanged; an empty key can never verify a real HMAC, so
    /// a caller that omits it simply gets "no contact" for any update that happens to carry one, never a
    /// false accept.
    /// </summary>
    public static ParsedMaxMessage? TryParse(MaxUpdate update, string botToken = "")
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

        // `25-151`: a MAX contact-attachment message may carry no `text` at all - the same "these two
        // are mutually exclusive on this provider's own wire shape" reasoning
        // TelegramInboundMessageParser's own remarks give for Telegram's identical case - so this can no
        // longer bail out on blank text alone; it must also ask whether a trustworthy contact rode along
        // instead.
        var contact = TryVerifyContact(update.Message.Body, botToken);

        if (string.IsNullOrWhiteSpace(text) && contact is null)
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

        return new ParsedMaxMessage(chatId, senderId, externalMessageId, text, contact);
    }

    /// <summary>
    /// `25-151`: MAX's own substitute for Telegram's `user_id` equality check - MAX attaches no sender
    /// identity to a contact attachment at all (this item's own honesty note: the public documentation
    /// does not confirm one exists), so the trust question here is answered entirely by
    /// <see cref="MaxContactAttachmentPayload.Hash"/>, an HMAC-SHA256 of
    /// <see cref="MaxContactAttachmentPayload.VcfInfo"/> keyed with the receiving site's own bot token -
    /// present, per MAX's own documentation outline, only when the visitor shared their own contact
    /// through a `request_contact`-type button (or shared it unprompted, the identical payload shape).
    /// A missing or non-matching hash is rejected here - returned as <see langword="null"/>,
    /// indistinguishable to this method's own caller from "no contact attachment was ever present" so the
    /// two can be told apart only by looking at the raw attachment list itself, exactly the same
    /// "caller re-inspects the raw shape to log a rejection distinctly from an absence" split
    /// <c>TelegramInboundMessageParser.TryVerifyContact</c>'s own remarks describe for Telegram.
    ///
    /// <para><b>Honesty note, repeated once more because this is the one part of this file with no
    /// public documentation and no live capture behind it at all</b> (unlike this file's own
    /// message-shape assumptions, which at least draw on third-party integration write-ups): the exact
    /// digest encoding MAX compares <see cref="MaxContactAttachmentPayload.Hash"/> against - lowercase
    /// hex, uppercase hex, or base64 - is this method's own guess (lowercase hex, the most common convention
    /// for an HMAC signature header across the providers this codebase already integrates with,
    /// <c>WhatsAppInboundMessageParser</c>'s own signature check included). A real MAX bot token and a
    /// real captured `request_contact` payload are what would confirm or correct it - see this item's own
    /// report.</para>
    /// </summary>
    private static ParsedMaxContact? TryVerifyContact(MaxMessageBody? body, string botToken)
    {
        var payload = body?.Attachments?
            .FirstOrDefault(a => a.Type == ContactAttachmentType)?
            .Payload;

        if (payload?.VcfInfo is not { } vcfInfo || string.IsNullOrWhiteSpace(vcfInfo))
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(payload.Hash) || string.IsNullOrEmpty(botToken))
        {
            return null;
        }

        var phone = payload.MaxInfo?.Phone;
        if (string.IsNullOrWhiteSpace(phone))
        {
            return null;
        }

        var computedHashHex = Convert.ToHexString(
            HMACSHA256.HashData(Encoding.UTF8.GetBytes(botToken), Encoding.UTF8.GetBytes(vcfInfo)));

        // Lowercased before the constant-time compare: this method's own honesty note above already
        // flags the digest's exact encoding as an assumption, and letter-casing is the one part of
        // "assumed hex" cheap enough to stop guessing about entirely rather than add to what a real
        // capture still has to confirm. Still a fixed-time byte compare, not string equality - an HMAC
        // verification is exactly the kind of check WhatsAppWebhookEndpoints/EmailWebhookEndpoints/
        // ChannelCredential.MatchesWebhookSecret already use one for, and there is no reason for this
        // provider to be the exception.
        var providedHashBytes = Encoding.UTF8.GetBytes(payload.Hash.Trim().ToLowerInvariant());
        var computedHashBytes = Encoding.UTF8.GetBytes(computedHashHex.ToLowerInvariant());
        if (providedHashBytes.Length != computedHashBytes.Length
            || !CryptographicOperations.FixedTimeEquals(providedHashBytes, computedHashBytes))
        {
            return null;
        }

        return new ParsedMaxContact(phone, payload.MaxInfo?.FirstName, payload.MaxInfo?.LastName);
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
///
/// <para><b>`25-151`:</b> <paramref name="Text"/> is nullable since this item - a contact-attachment
/// message may carry none at all (this type's own remarks on
/// <see cref="MaxInboundMessageParser.TryParse"/>'s new behaviour). <paramref name="Text"/> and
/// <paramref name="Contact"/> are not mutually exclusive the way Telegram's own two fields are (MAX's
/// documentation gives no reason to assume they cannot coexist on one message), so the caller
/// (<see cref="MaxLongPollingService"/>/<c>MaxWebhookEndpoints</c>) acts on whichever of the two is
/// present, independently.</para>
/// </summary>
public sealed record ParsedMaxMessage(
    long ChatId, long SenderId, string ExternalMessageId, string? Text, ParsedMaxContact? Contact = null);

/// <summary>
/// `25-151`: a MAX contact that has already passed <see cref="MaxInboundMessageParser"/>'s own
/// HMAC-SHA256 trust check - by the time this type exists, "did this site's own bot actually hand out
/// this contact" is already answered, so nothing downstream needs to re-ask it.
/// </summary>
public sealed record ParsedMaxContact(string Phone, string? FirstName, string? LastName);
