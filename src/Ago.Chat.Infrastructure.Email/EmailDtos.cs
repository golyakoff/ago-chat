using System.Text.Json.Serialization;

namespace Ago.Chat.Infrastructure.Email;

// `14-09`: everything below the Infrastructure boundary for this channel
// (ChannelPortTests.NoProviderVocabulary_AppearsAboveInfrastructure's own list has no "Email" entry to add
// to - see ChannelKind.Email's own remarks for why this channel introduces no vendor vocabulary at all).
//
// **Honesty note, the same discipline MaxDtos.cs/VkDtos.cs/WhatsAppDtos.cs each state for themselves, and
// the most important one in this file.** Every other channel's own wire shape is confirmed against a real
// provider's own documentation or SDK source. This one is not, because there is no third-party inbound-
// parse provider in play at all: `10-05` chose to self-host mail entirely, with no SES/Mailgun/SendGrid/
// Postmark-style inbound-parse webhook anywhere in the picture, and that item's own Out-of-scope section
// says plainly there is "no IMAP, no webmail, ... and none planned" beyond a few RFC 2142 aliases delivering
// to one local mbox. So the wire shape below is **this item's own invented contract** for a future,
// currently-unbuilt piece: a small script, run as a Postfix pipe-transport destination for
// `support+*@{domain}`, that turns one raw SMTP `DATA` payload into exactly this JSON and POSTs it to
// `EmailWebhookEndpoints`. Building that script is real, concrete, deploy-side work - it needs a MIME
// parser, the domain's own `recipient_delimiter = +` setting, and a Postfix transport map entry - and it is
// explicitly **out of this item's own scope** (ago-deploy, per this task's own brief). What this item
// builds is everything on AGO Chat's own side of that boundary: the contract the script would need to
// satisfy, and the whole pipeline that consumes it once it does. This is the same honest-gap shape `14-10`'s
// own WhatsApp item and `19-01` each already use for "not verified live against the real provider" - here
// applied one level earlier, to "the real provider does not exist in this environment at all".
//
// A single message per delivery, not a batch container the way WhatsAppWebhookEnvelope is - one SMTP
// `DATA` transaction is one message, unlike Meta's own webhook, which can genuinely batch several Cloud API
// events into one HTTP call.

/// <summary>One inbound email, already MIME-decoded by whatever produces this payload (this file's own
/// honesty note explains what that is and why it does not exist yet). Every field is a plain string because
/// MIME decoding (charset, transfer-encoding, multipart walking) is exactly the kind of provider-shaped
/// detail `adr/0006`'s "largest common denominator that does not lie" keeps below this boundary - this type
/// itself is the boundary, not a participant in it.</summary>
public sealed record EmailInboundWebhookPayload(
    [property: JsonPropertyName("from")] string? From,
    [property: JsonPropertyName("to")] string? To,
    [property: JsonPropertyName("subject")] string? Subject,
    [property: JsonPropertyName("text")] string? Text,
    [property: JsonPropertyName("messageId")] string? MessageId);
