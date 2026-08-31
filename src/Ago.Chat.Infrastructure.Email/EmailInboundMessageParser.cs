using Ago.Chat.Domain;

namespace Ago.Chat.Infrastructure.Email;

/// <summary>
/// `14-09`: the one place an <see cref="EmailInboundWebhookPayload"/> becomes something worth acting on -
/// a pure function, the same shape <c>MaxInboundMessageParser</c>/<c>VkInboundMessageParser</c>/
/// <c>WhatsAppInboundMessageParser</c> already establish, used by <c>EmailWebhookEndpoints</c>.
///
/// <para><b>Site resolution lives here too, unlike every precedent.</b> MAX/Telegram/VK resolve their
/// tenant from a <c>{credentialId}</c> URL path segment; WhatsApp resolves it from a repository lookup
/// keyed by <c>phone_number_id</c>. Email's routing (<see cref="EmailRecipientAddress"/>'s own remarks) is
/// a pure function of the recipient address and this deployment's own configuration - no I/O, no
/// repository - so folding it into this parser rather than leaving it to the endpoint keeps the endpoint's
/// own job identical to every other channel's: authenticate, parse, hand a fully-resolved command to
/// <c>ReceiveChannelMessageHandler</c>. <see cref="EmailWebhookEndpoints"/> still independently confirms
/// the parsed <see cref="SiteId"/> names a real site before doing anything with it
/// (<see cref="EmailRecipientAddress"/>'s own remarks on why a parseable id is not proof of existence) -
/// that check needs a repository, so it could not live in this pure function either way.</para>
/// </summary>
public static class EmailInboundMessageParser
{
    /// <summary>The subject a message with no <c>Subject</c> header (or an empty one) is given -
    /// <see cref="EmailThreadState.Subject"/> must never be empty (its own column is <c>NOT NULL</c>),
    /// and a visitor who genuinely sent no subject is a real, unremarkable case, not a parse failure.</summary>
    public const string DefaultSubject = "(no subject)";

    public static ParsedEmailMessage? Parse(EmailInboundWebhookPayload payload, EmailBotApiOptions options)
    {
        if (payload.From is not { Length: > 0 } from)
        {
            return null;
        }

        if (payload.MessageId is not { Length: > 0 } messageId)
        {
            // Domain.ExternalMessageId's own idempotency contract, and this channel's own threading
            // contract (EmailThreadState), both need this - a message with no Message-ID has nothing this
            // system could deduplicate a redelivery on, or thread a reply against later.
            return null;
        }

        if (string.IsNullOrWhiteSpace(payload.Text))
        {
            return null;
        }

        var siteId = EmailRecipientAddress.TryParseSiteId(options, payload.To);
        if (siteId is not { } resolvedSiteId)
        {
            return null;
        }

        var subject = string.IsNullOrWhiteSpace(payload.Subject) ? DefaultSubject : payload.Subject.Trim();

        return new ParsedEmailMessage(resolvedSiteId, from.Trim(), messageId.Trim(), subject, payload.Text);
    }
}

/// <summary><paramref name="SiteId"/> is what <see cref="EmailInboundMessageParser"/> resolves the tenant
/// by, from the recipient address alone - see that type's own remarks and
/// <see cref="EmailRecipientAddress"/>'s own remarks for why this channel needs no repository lookup to do
/// it, unlike every channel before it. <paramref name="From"/> is the visitor's own email address - what
/// this system stores as the <see cref="ExternalChannelAddress"/> and what every outbound reply is
/// sent back to.</summary>
public sealed record ParsedEmailMessage(SiteId SiteId, string From, string ExternalMessageId, string Subject, string Text);
