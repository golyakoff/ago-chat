using System.Text.Json;

namespace Ago.Chat.Infrastructure.Vk;

/// <summary>
/// `14-08`: the one place a <see cref="VkCallbackEvent"/> becomes something worth acting on - a pure
/// function, the same shape <c>MaxInboundMessageParser</c>/<c>TelegramInboundMessageParser</c> already
/// establish, used by <c>VkWebhookEndpoints</c> (this channel's only inbound mechanism - there is no
/// second, polling-loop caller the way MAX's poller shares its own parser).
///
/// <para>Recognises only <see cref="VkCallbackEventTypes.MessageNew"/> - VK's own Callback API carries
/// dozens of other event kinds (a wall post, a community join, a photo comment) this item has no use
/// case for; every other kind, and any event whose payload does not match the expected shape, returns
/// <see langword="null"/> rather than throwing, the identical "skip what we do not understand and keep
/// going" reasoning <c>MaxInboundMessageParser.TryParse</c>'s own remarks state.</para>
///
/// <para><b>Filtering <c>out == 1</c> is the one rule with no MAX/Telegram equivalent, and it is not
/// optional.</b> MAX's webhook and Telegram's <c>getUpdates</c> only ever surface messages sent <em>to</em>
/// the bot - there is nothing in either provider's own shape for this system's own replies to loop back
/// through. VK's Callback API is different: <c>message_new</c> fires for a community's own outgoing
/// messages too, marked by <c>message.out == 1</c> (VkDtos.cs's own remarks on <see cref="VkMessage.Out"/>).
/// Without this filter, the moment an operator sends a reply through <c>VkChannelAdapter.SendAsync</c>,
/// VK would deliver that very message back to this webhook as a fresh <c>message_new</c> event, and this
/// parser would treat AGO's own reply as a new inbound visitor message - creating a message that quotes
/// itself back into the conversation on every operator reply, forever. This is exactly the class of bug
/// `14-02`'s own item found live only once a real bot existed (MAX's <c>chat_id</c> vs. <c>sender.user_id</c>
/// mixup); this one is caught here, from VK's own documented event shape, before any live token exists
/// at all.</para>
/// </summary>
public static class VkInboundMessageParser
{
    /// <summary>`25-166`: VK's own attachment-type discriminator for a photo - confirmed against VK's
    /// own published API reference, <c>VkAttachment</c>'s own remarks.</summary>
    private const string PhotoAttachmentType = "photo";

    public static ParsedVkMessage? TryParse(VkCallbackEvent callbackEvent)
    {
        if (callbackEvent.Type != VkCallbackEventTypes.MessageNew || callbackEvent.Object is not { } payload)
        {
            return null;
        }

        VkMessageNewObject? messageNew;
        try
        {
            messageNew = payload.Deserialize<VkMessageNewObject>();
        }
        catch (JsonException)
        {
            return null;
        }

        var message = messageNew?.Message;
        if (message is null)
        {
            return null;
        }

        // See this class's own remarks - a community's own outgoing message, echoed back by VK's own
        // Callback API, must never be treated as a new inbound visitor message.
        if (message.Out is 1)
        {
            return null;
        }

        if (message.FromId is not { } fromId)
        {
            return null;
        }

        if (message.PeerId is not { } peerId)
        {
            return null;
        }

        var text = message.Text;

        // `25-166`: a VK photo message may carry no `text` at all - the identical "these two are not
        // mutually exclusive on this provider's own wire shape" reasoning `ParsedMaxMessage`'s own
        // remarks give for MAX's captioned photo (VK's own message object carries `text` and
        // `attachments` as independent fields, both populated together for a captioned photo) - so this
        // can no longer bail out on blank text alone; it must also ask whether a resolvable photo rode
        // along instead.
        var image = TryExtractImage(message.Attachments);

        if (string.IsNullOrWhiteSpace(text) && image is null)
        {
            return null;
        }

        // The provider's own message id is the idempotency key ExternalMessageId.ToClientMessageId
        // relies on (14-01's own design) - a fallback synthesised from peer+date is used only if this
        // parser ever sees `id` missing or zero, which VK's own documentation does not confirm it does
        // or does not do (MaxInboundMessageParser's own remarks make the identical trade-off for MAX's
        // `body.mid`).
        var externalMessageId = message.Id is > 0 ? message.Id.Value.ToString() : $"{peerId}:{message.Date ?? 0}";

        return new ParsedVkMessage(peerId, fromId, externalMessageId, text, image);
    }

    /// <summary>
    /// `25-166`: the inbound half of this item's own diagnosis - before this method existed, nothing in
    /// this file ever read <see cref="VkMessage.Attachments"/> at all, so a sent photo's own attachment
    /// entry was invisible to <see cref="TryParse"/> no matter what it carried; a caption-less photo (no
    /// `text`) then failed the blank-body bail-out too, dropping the entire inbound message - the
    /// identical live symptom `25-161`'s own report found for MAX.
    ///
    /// <para>Picks the highest resolution by <see cref="VkPhotoSize.Width"/> explicitly, not by array
    /// position or <see cref="VkPhotoSize.Type"/>'s own letter code - <see cref="VkPhoto"/>'s own remarks
    /// on why neither is a documented ordering guarantee. A size with no <see cref="VkPhotoSize.Url"/> at
    /// all is skipped defensively, the same "an unconfirmed shape degrades to skipped, never guessed at"
    /// posture <c>MaxInboundMessageParser</c>/<c>TelegramInboundMessageParser</c> already hold for their
    /// own equivalents.</para>
    ///
    /// <para>No trust check the way <c>TelegramInboundMessageParser.TryVerifyContact</c> needs one for a
    /// contact: VK itself is the one giving this system the photo URL, inside an event this parser's own
    /// caller already authenticated (<c>VkWebhookEndpoints</c>' own <c>secret</c> field check), the
    /// identical trust boundary <see cref="VkMessage.Text"/> already crosses with no separate
    /// verification of its own.</para>
    /// </summary>
    private static ParsedVkImage? TryExtractImage(IReadOnlyList<VkAttachment>? attachments)
    {
        var sizes = attachments?
            .FirstOrDefault(a => a.Type == PhotoAttachmentType)?
            .Photo?.Sizes;

        if (sizes is null || sizes.Count == 0)
        {
            return null;
        }

        var best = sizes
            .Where(size => !string.IsNullOrWhiteSpace(size.Url))
            .OrderByDescending(size => size.Width ?? 0)
            .FirstOrDefault();

        return best?.Url is { } url ? new ParsedVkImage(url) : null;
    }
}

/// <summary>
/// <paramref name="PeerId"/> is what this system stores as the <c>ChannelIdentity</c>'s own external
/// address and what every outbound reply is sent to - VK's own <c>messages.send</c> operates on
/// <c>peer_id</c>, not <c>from_id</c>; for a private 1:1 conversation with a community the two happen to
/// be numerically related but are not interchangeable in general (VK's own <c>peer_id</c> convention
/// reserves higher ranges for chats), so this parser keeps them distinct on principle even though this
/// item only ever sees the 1:1 case. <paramref name="FromId"/> is kept for whichever future caller needs
/// to know who specifically wrote a message (nothing does yet) - the identical shape
/// <c>ParsedMaxMessage.SenderId</c> already establishes.
///
/// <para><b>`25-166`:</b> <paramref name="Text"/> is nullable since this item - a caption-less photo
/// carries none at all (this type's own remarks on <see cref="VkInboundMessageParser.TryParse"/>'s new
/// behaviour). <paramref name="Image"/> is not mutually exclusive with <paramref name="Text"/> - a
/// captioned photo produces both, independently, the same shape <c>ParsedMaxMessage</c>/
/// <c>ParsedTelegramMessage</c> already establish for their own captioned photo.</para>
/// </summary>
public sealed record ParsedVkMessage(
    long PeerId, long FromId, string ExternalMessageId, string? Text, ParsedVkImage? Image = null);

/// <summary>
/// `25-166`: a VK photo attachment this parser could actually resolve to a directly fetchable URL - see
/// <see cref="VkInboundMessageParser"/>'s own <c>TryExtractImage</c> remarks for why the highest
/// resolution's URL is the only field this record carries forward. The caller
/// (<c>Ago.Chat.Api.Channels.VkWebhookEndpoints</c>) is what actually downloads the bytes
/// (<see cref="VkApiClient.DownloadImageAsync"/>) and hands them to
/// <c>Ago.Chat.Application.UseCases.ReceiveChannelAttachment.ReceiveChannelAttachmentHandler</c> - this
/// type itself carries no bytes and does no I/O, matching <c>ParsedMaxImage</c>'s/<c>ParsedTelegramImage</c>'s
/// own shape.
/// </summary>
public sealed record ParsedVkImage(string Url);
