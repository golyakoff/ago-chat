namespace Ago.Chat.Infrastructure.Avito;

/// <summary>
/// `14-11`: the one place an <see cref="AvitoWebhookEnvelope"/> becomes something worth acting on - a
/// pure function, the same shape <c>MaxInboundMessageParser</c>/<c>VkInboundMessageParser</c>/
/// <c>WhatsAppInboundMessageParser</c> already establish, used by <c>AvitoWebhookEndpoints</c> (this
/// channel's only inbound mechanism - Avito's own Messenger API documentation, per its OpenAPI schema,
/// offers no polling alternative to the webhook, the identical "webhook only" shape `14-08` found for
/// VK).
///
/// <para><b>Returns a single nullable result, not a list</b> - unlike WhatsApp's natively-batched
/// envelope, Avito's own schema delivers exactly one message per HTTP call (<c>AvitoDtos.cs</c>'s own
/// remarks), the same single-event shape MAX's and VK's own wire formats already have.</para>
///
/// <para><b>Filtering the seller's own outgoing message is inferred, not read from an explicit flag -
/// the one place this item's own design differs from its nearest precedent, VK's <c>message.out</c>.</b>
/// Avito's own schema, per this item's own research, has no equivalent boolean on
/// <see cref="AvitoWebhookMessage"/>. What it does have is <see cref="AvitoWebhookMessage.UserId"/>,
/// documented as "always the account the webhook is registered to", and
/// <see cref="AvitoWebhookMessage.AuthorId"/>, the message's own sender. A message whose
/// <c>AuthorId == UserId</c> is therefore, by construction, one the webhook-owning seller account itself
/// sent - whether through an operator's own reply via <see cref="AvitoChannelAdapter.SendAsync"/> or
/// through the seller messaging a buyer directly inside Avito's own app - and either way it is not a
/// visitor message an operator needs to answer. Without this filter, the moment an operator replied
/// through this channel, Avito would deliver that very message back to this webhook as a fresh delivery
/// (the same reply-loop hazard <c>VkInboundMessageParser</c>'s own remarks describe for VK), and this
/// parser would treat AGO's own reply as a new inbound visitor message.</para>
///
/// <para><b><c>chat_type == "a2u"</c> (a chat with Avito itself, not a customer) is filtered out</b> -
/// this item's own scope is buyer-seller messages only (this item's own Out of scope section); an
/// <c>a2u</c> chat has no real visitor on the other end, so routing it into
/// <see cref="Application.UseCases.ReceiveChannelMessage.ReceiveChannelMessageHandler"/> would create a
/// "visitor" that is actually Avito's own system. <c>u2i</c> (listing-scoped) and <c>u2u</c>
/// (profile-scoped) are both real buyer conversations and both accepted - see this file's own remarks and
/// <see cref="AvitoChannelAdapter"/>'s own remarks for why the distinction between them is not carried
/// any further than this filter.</para>
///
/// <para>Only <see cref="AvitoWebhookMessage.Type"/> <c>"text"</c> is recognised - the identical
/// "recognise the one shape this item handles, skip the rest" restraint every precedent in this stage
/// applies to its own provider's message-type breadth (image, voice, call, location, a system bot
/// message, a shared listing card).</para>
/// </summary>
public static class AvitoInboundMessageParser
{
    public static ParsedAvitoMessage? Parse(AvitoWebhookEnvelope envelope)
    {
        if (envelope.Payload?.Type != "message" || envelope.Payload.Value is not { } message)
        {
            return null;
        }

        if (message.ChatType == AvitoChatTypes.Avito)
        {
            return null;
        }

        if (message.AuthorId is { } authorId && message.UserId is { } userId && authorId == userId)
        {
            return null;
        }

        if (message.Type != AvitoMessageTypes.Text)
        {
            return null;
        }

        if (message.ChatId is not { Length: > 0 } chatId)
        {
            return null;
        }

        var text = message.Content?.Text;
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        // The provider's own message id is the idempotency key ExternalMessageId.ToClientMessageId
        // relies on (`14-01`'s own design). This item found no confirmed guarantee, the way WhatsApp's
        // Cloud API documentation showed `id` present on every example, that Avito's own `id` is always
        // populated - a message this parser somehow sees without one is skipped rather than assigned a
        // synthesised id, matching WhatsApp's own conservative choice rather than MAX's/VK's own
        // fallback (WhatsAppInboundMessageParser's own remarks have the identical reasoning).
        if (message.Id is not { Length: > 0 } externalMessageId)
        {
            return null;
        }

        return new ParsedAvitoMessage(chatId, message.UserId, text, externalMessageId);
    }
}

/// <summary>
/// <paramref name="ChatId"/> is what this system stores as the <c>ChannelIdentity</c>'s own external
/// address and what every outbound reply is sent to (<c>AvitoChannelAdapter</c>'s own remarks on why this
/// is the address rather than <see cref="AvitoWebhookMessage.AuthorId"/>). <paramref name="WebhookOwnerUserId"/>
/// is the account the webhook is registered to (<see cref="AvitoWebhookMessage.UserId"/>) - carried
/// through only for <c>AvitoWebhookEndpoints</c>'s own sanity check against the credential its own URL
/// path segment already named; nothing routes on it.
/// </summary>
public sealed record ParsedAvitoMessage(string ChatId, long? WebhookOwnerUserId, string Text, string ExternalMessageId);
