namespace Ago.Chat.Infrastructure.WhatsApp;

/// <summary>
/// `14-10`: the one place a <see cref="WhatsAppWebhookEnvelope"/> becomes something worth acting on - a
/// pure function, the same shape <c>MaxInboundMessageParser</c>/<c>TelegramInboundMessageParser</c>/
/// <c>VkInboundMessageParser</c> already establish, used by <c>WhatsAppWebhookEndpoints</c> (this
/// channel's only inbound mechanism - Meta's Cloud API has no polling alternative, the identical "webhook
/// only" shape `14-08` found for VK, unlike MAX's/Telegram's own webhook-plus-poll designs).
///
/// <para><b>Returns a list, not a single nullable result - the one shape difference from every
/// precedent.</b> <see cref="WhatsAppEntry"/>'s own remarks explain why: Meta's own webhook envelope is
/// natively a batch container (<c>entry[]</c>, each with its own <c>changes[]</c>, each potentially
/// carrying several <c>messages[]</c>), where MAX's and VK's own wire shapes deliver exactly one event
/// per call. A parser that only read <c>entry[0].changes[0].value.messages[0]</c> would silently drop
/// every message after the first the one time Meta actually batches two together - a real, if
/// undocumented-as-common, delivery shape this item chooses not to gamble on.</para>
///
/// <para><b>Filtering out status-only deliveries is implicit, not a flag check - <see cref="WhatsAppChangeValue"/>'s
/// own remarks have the full reasoning.</b> A change whose <c>value</c> carries <see cref="WhatsAppChangeValue.Statuses"/>
/// instead of <see cref="WhatsAppChangeValue.Messages"/> - Meta's own delivery-receipt callback for an
/// operator's own outbound reply - is skipped simply because this parser only ever reads
/// <see cref="WhatsAppChangeValue.Messages"/>, the WhatsApp-shaped answer to the same hazard
/// <c>VkInboundMessageParser</c>'s own <c>out == 1</c> check solves for VK.</para>
///
/// <para>Only <see cref="WhatsAppMessage.Type"/> <c>"text"</c> is recognised - <see cref="WhatsAppMessage"/>'s
/// own remarks explain the scope cut; every other type (image, audio, location, an interactive reply) is
/// skipped rather than coerced into a text-shaped stand-in.</para>
/// </summary>
public static class WhatsAppInboundMessageParser
{
    public static IReadOnlyList<ParsedWhatsAppMessage> Parse(WhatsAppWebhookEnvelope envelope)
    {
        var results = new List<ParsedWhatsAppMessage>();

        foreach (var entry in envelope.Entry ?? [])
        {
            foreach (var change in entry.Changes ?? [])
            {
                var value = change.Value;
                var phoneNumberId = value?.Metadata?.PhoneNumberId;
                if (phoneNumberId is not { Length: > 0 })
                {
                    continue;
                }

                foreach (var message in value?.Messages ?? [])
                {
                    var parsed = TryParseOne(phoneNumberId, message);
                    if (parsed is not null)
                    {
                        results.Add(parsed);
                    }
                }
            }
        }

        return results;
    }

    private static ParsedWhatsAppMessage? TryParseOne(string phoneNumberId, WhatsAppMessage message)
    {
        if (message.Type != "text")
        {
            return null;
        }

        if (message.From is not { Length: > 0 } from)
        {
            return null;
        }

        var text = message.Text?.Body;
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        // The provider's own message id is the idempotency key ExternalMessageId.ToClientMessageId
        // relies on (14-01's own design). Unlike MAX's/VK's own fallback for a missing id, Meta's own
        // documentation shows `id` present on every message example this item's own research found -
        // no fallback is built here, and a message this parser somehow sees without one is skipped
        // rather than assigned a synthesised id, since nothing about WhatsApp's own documentation
        // suggested that case is real the way MAX's/VK's own uncertainty about their fields was.
        if (message.Id is not { Length: > 0 } externalMessageId)
        {
            return null;
        }

        return new ParsedWhatsAppMessage(phoneNumberId, from, externalMessageId, text);
    }
}

/// <summary>
/// <paramref name="PhoneNumberId"/> is what <c>WhatsAppWebhookEndpoints</c> resolves a tenant by
/// (<c>IChannelCredentialRepository.GetActiveByProviderAccountIdAsync</c>) - see this parser's own
/// remarks and <see cref="WhatsAppMetadata"/>'s own remarks for why WhatsApp needs this where no other
/// channel does. <paramref name="From"/> is the visitor's own WhatsApp phone number - what this system
/// stores as the <see cref="Domain.ExternalChannelAddress"/> and what every outbound reply is sent back
/// to.
/// </summary>
public sealed record ParsedWhatsAppMessage(string PhoneNumberId, string From, string ExternalMessageId, string Text);
