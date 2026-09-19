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
/// <para><see cref="WhatsAppMessage.Type"/> <c>"text"</c> and (`25-165`) <c>"image"</c> are recognised -
/// <see cref="WhatsAppMessage"/>'s own remarks explain both the original scope cut and why "image"
/// specifically no longer belongs on the skip list; audio, location, an interactive reply and every
/// other type are still skipped rather than coerced into a text-shaped stand-in.</para>
/// </summary>
public static class WhatsAppInboundMessageParser
{
    private const string TextMessageType = "text";

    /// <summary>`25-165`: WhatsApp's own type discriminator for an inbound image - confirmed against
    /// Meta's own Cloud API webhooks documentation, <c>WhatsAppMediaObject</c>'s own remarks.</summary>
    private const string ImageMessageType = "image";

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
        if (message.Type != TextMessageType && message.Type != ImageMessageType)
        {
            return null;
        }

        if (message.From is not { Length: > 0 } from)
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

        string? text;
        ParsedWhatsAppImage? image = null;

        if (message.Type == TextMessageType)
        {
            text = message.Text?.Body;
            if (string.IsNullOrWhiteSpace(text))
            {
                return null;
            }
        }
        else
        {
            // `25-165`: WhatsApp never populates `text` on an image message - a captioned image's own
            // prose lives in the image object's own `caption` field (`WhatsAppMediaObject`'s own
            // remarks). Folded into `Text` here so a captioned image still produces a plain-text
            // message alongside its own attachment, independently - the same shape a captioned
            // MAX/Telegram photo already produces, reached by a different wire route.
            image = TryExtractImage(message.Image);
            if (image is null)
            {
                // An "image" type whose own payload carries no usable media_id is not something Meta's
                // own documentation describes happening, but there is nothing this parser could do with
                // it either way - skipped, the same "an unconfirmed shape degrades to skipped, never
                // guessed at" posture MaxInboundMessageParser/TelegramInboundMessageParser already hold
                // for their own equivalents.
                return null;
            }

            text = message.Image?.Caption;
        }

        return new ParsedWhatsAppMessage(phoneNumberId, from, externalMessageId, text, image);
    }

    /// <summary>`25-165`: the inbound half of this item's own diagnosis - before this method existed,
    /// nothing in this file ever read <see cref="WhatsAppMessage.Image"/> at all
    /// (<see cref="WhatsAppMessage"/>'s own remarks on the original, deliberate scope cut this method
    /// addresses). No trust check the way <c>TelegramInboundMessageParser.TryVerifyContact</c> needs one
    /// for a contact: Meta itself is the one giving this system the <c>media_id</c>, inside a delivery
    /// this parser's own caller already authenticated (<c>WhatsAppWebhookEndpoints</c>' own
    /// <c>X-Hub-Signature-256</c> check), the identical trust boundary <see cref="WhatsAppMessage.Text"/>
    /// already crosses with no separate verification of its own.</summary>
    private static ParsedWhatsAppImage? TryExtractImage(WhatsAppMediaObject? image) =>
        image?.Id is { Length: > 0 } mediaId ? new ParsedWhatsAppImage(mediaId) : null;
}

/// <summary>
/// <paramref name="PhoneNumberId"/> is what <c>WhatsAppWebhookEndpoints</c> resolves a tenant by
/// (<c>IChannelCredentialRepository.GetActiveByProviderAccountIdAsync</c>) - see this parser's own
/// remarks and <see cref="WhatsAppMetadata"/>'s own remarks for why WhatsApp needs this where no other
/// channel does. <paramref name="From"/> is the visitor's own WhatsApp phone number - what this system
/// stores as the <see cref="Domain.ExternalChannelAddress"/> and what every outbound reply is sent back
/// to.
///
/// <para><b>`25-165`:</b> <paramref name="Text"/> is nullable since this item - an image message with no
/// caption carries none at all (this type's own remarks on <see cref="WhatsAppInboundMessageParser.TryParseOne"/>'s
/// new behaviour). <paramref name="Image"/> is not mutually exclusive with <paramref name="Text"/> the
/// way it would be if WhatsApp's own wire shape allowed one message to carry both a `"text"`-type body
/// and an `"image"`-type payload (it does not - <see cref="WhatsAppMessage.Type"/> is one discriminator,
/// never both at once), but a captioned image folds its caption into <paramref name="Text"/>
/// (<see cref="WhatsAppInboundMessageParser.TryParseOne"/>'s own remarks), so the caller acts on
/// whichever is present, independently - the same shape <c>ParsedMaxMessage</c>/<c>ParsedTelegramMessage</c>
/// already establish for their own captioned photo.</para>
/// </summary>
public sealed record ParsedWhatsAppMessage(
    string PhoneNumberId, string From, string ExternalMessageId, string? Text, ParsedWhatsAppImage? Image = null);

/// <summary>
/// `25-165`: a WhatsApp image attachment this parser could actually resolve to a <c>media_id</c> - see
/// <see cref="WhatsAppInboundMessageParser"/>'s own <c>TryExtractImage</c> remarks. The caller
/// (<c>Ago.Chat.Api.Channels.WhatsAppWebhookEndpoints</c>) is what actually resolves and downloads the
/// bytes (<see cref="WhatsAppApiClient.DownloadImageAsync"/>'s own two-step protocol) and hands them to
/// <c>Ago.Chat.Application.UseCases.ReceiveChannelAttachment.ReceiveChannelAttachmentHandler</c> - this
/// type itself carries no bytes and does no I/O, matching <c>ParsedMaxImage</c>'s/<c>ParsedTelegramImage</c>'s
/// own shape.
/// </summary>
public sealed record ParsedWhatsAppImage(string MediaId);
