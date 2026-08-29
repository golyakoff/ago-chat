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
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        // The provider's own message id is the idempotency key ExternalMessageId.ToClientMessageId
        // relies on (14-01's own design) - a fallback synthesised from peer+date is used only if this
        // parser ever sees `id` missing or zero, which VK's own documentation does not confirm it does
        // or does not do (MaxInboundMessageParser's own remarks make the identical trade-off for MAX's
        // `body.mid`).
        var externalMessageId = message.Id is > 0 ? message.Id.Value.ToString() : $"{peerId}:{message.Date ?? 0}";

        return new ParsedVkMessage(peerId, fromId, externalMessageId, text);
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
/// </summary>
public sealed record ParsedVkMessage(long PeerId, long FromId, string ExternalMessageId, string Text);
