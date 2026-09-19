using Ago.Chat.Application.Abstractions;
using Ago.Chat.Domain;
using Ago.Platform.Kernel;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Ago.Chat.Infrastructure.Email;

/// <summary>
/// `14-09`: email's own implementation of `14-01`'s <see cref="IInboundChannelAdapter"/> - the sixth real
/// adapter, modelled on <c>WhatsAppChannelAdapter</c>/<c>VkChannelAdapter</c> for the overall shape, but
/// genuinely different in two ways neither precedent has: no <see cref="Domain.ChannelCredential"/> lookup
/// at all (<see cref="EmailBotApiOptions"/>' own remarks on why this channel has no per-tenant secret), and
/// a real per-conversation state read (<see cref="IEmailThreadStore"/>) needed to build correct threading
/// headers, which no other channel's outbound send has ever needed.
///
/// <para><b>Why this is a singleton that opens its own DI scope, rather than a scoped class.</b> The
/// identical reasoning <c>WhatsAppChannelAdapter</c>'s own remarks give: the singleton
/// <c>InboundChannelAdapterRegistry</c> is built from <c>IEnumerable&lt;IInboundChannelAdapter&gt;</c>, so
/// every adapter it holds must be safe to keep for the process lifetime, while <see cref="IConversationRepository"/>
/// and <see cref="IEmailThreadStore"/> are both <c>Scoped</c> - so this class takes
/// <see cref="IServiceScopeFactory"/> and opens one scope per <see cref="SendAsync"/> call.</para>
///
/// <para><b>No <see cref="Domain.ChannelCredential"/> lookup - the central shape difference from every
/// channel before this one.</b> MAX/Telegram/VK/WhatsApp each resolve the site's own active credential to
/// find the token (and, for VK/WhatsApp, a second provider-owned identifier) needed to make the outbound
/// call. Email has nothing of that shape to resolve: <see cref="EmailBotApiOptions"/> is deployment-wide
/// configuration, not a per-site secret, so the one site-specific fact this method needed before `25-156` -
/// <see cref="Domain.SiteId"/>, to build the site's own <c>support+{siteId}@{domain}</c> sender address
/// (<see cref="EmailRecipientAddress"/>'s own remarks) - came from loading the <see cref="Conversation"/>
/// alone, the same lookup <c>WhatsAppChannelAdapter</c>'s own remarks describe needing for the identical
/// reason (<see cref="OutboundChannelMessage"/> carries no <c>SiteId</c> of its own).</para>
///
/// <para><b>`25-156`: a second, real per-tenant lookup joins it - <see cref="ISiteRepository.GetByIdAsync"/>.</b>
/// Branding the reply in the tenant's own name and colour (<see cref="TenantReplyEmailShell"/>) needs
/// <see cref="Site.Name"/> and <see cref="WidgetConfig.PrimaryColorHex"/>, neither of which the
/// <see cref="Conversation"/> aggregate itself carries - so this method now loads both aggregates in the
/// same scope, on the identical "should not happen if missing" terms the <see cref="EmailThreadState"/>
/// lookup just below already established for itself.</para>
///
/// <para><b>A missing <see cref="EmailThreadState"/> row is thrown, not refused - the identical "should not
/// happen" treatment every other adapter's own missing-conversation case gets.</b> A conversation can only
/// exist on the <see cref="ChannelKind.Email"/> channel because an inbound message created it
/// (`adr/0027`'s "AGO Inbox is not a third product" - every conversation starts from
/// <c>ReceiveChannelMessageHandler</c>), and <c>EmailWebhookEndpoints</c> always writes an
/// <see cref="EmailThreadState"/> row in the same request that resolves the conversation
/// (<see cref="EmailThreadState"/>'s own remarks). So a conversation on this channel with no thread state
/// is not a real, reachable outcome - a caller bug or a data inconsistency, the same category
/// <c>MaxChannelAdapter</c>'s/<c>WhatsAppChannelAdapter</c>'s own missing-conversation cases already get,
/// thrown rather than surfaced as an ordinary <see cref="ChannelSendOutcome.Refused"/>.</para>
///
/// <para><b>Refused-vs-thrown for the send itself is entirely <see cref="EmailSmtpClient"/>'s own call</b> -
/// this method only translates <see cref="EmailSendResult"/> into <see cref="ChannelSendOutcome"/>, the
/// identical thin translation <c>WhatsAppChannelAdapter</c>'s own final lines perform for
/// <c>WhatsAppSendResult</c>.</para>
/// </summary>
public sealed class EmailChannelAdapter(
    EmailSmtpClient client, IOptions<EmailBotApiOptions> options, IServiceScopeFactory scopeFactory,
    IClock clock, ILogger<EmailChannelAdapter> logger) : IInboundChannelAdapter
{
    public ChannelKind Kind => ChannelKind.Email;

    public async Task<ChannelSendOutcome> SendAsync(OutboundChannelMessage message, CancellationToken cancellationToken)
    {
        string fromAddress;
        EmailThreadState thread;
        Site site;

        await using (var scope = scopeFactory.CreateAsyncScope())
        {
            var conversations = scope.ServiceProvider.GetRequiredService<IConversationRepository>();
            var threads = scope.ServiceProvider.GetRequiredService<IEmailThreadStore>();
            var sites = scope.ServiceProvider.GetRequiredService<ISiteRepository>();

            var conversation = await conversations.GetByIdAsync(message.ConversationId, cancellationToken);
            if (conversation is null)
            {
                // Should not happen: DeliverChannelMessageHandler just loaded this same conversation to
                // build the message it handed to this adapter - WhatsAppChannelAdapter's own remarks
                // explain why this is thrown rather than refused.
                throw new InvalidOperationException(
                    $"Conversation {message.ConversationId.Value} was not found while relaying a message to Email.");
            }

            var threadState = await threads.GetAsync(message.ConversationId, cancellationToken);
            if (threadState is null)
            {
                // Should not happen - this type's own remarks explain why an email conversation with no
                // thread state is a data inconsistency, not a reachable ordinary outcome.
                throw new InvalidOperationException(
                    $"Conversation {message.ConversationId.Value} has no EmailThreadState, but is on the Email channel.");
            }

            // `25-156`: the one new lookup this item adds - TenantReplyEmailShell needs the tenant's own
            // Site.Name and WidgetConfig.PrimaryColorHex, neither of which OutboundChannelMessage or
            // Conversation itself carries. A conversation's own SiteId always names a real Site (the
            // same "a conversation cannot exist without its tenant" fact EmailRecipientAddress.Build
            // already relies on for the same conversation.SiteId, just below) - so a missing Site here is
            // the identical "should not happen, thrown rather than silently accepted" data-inconsistency
            // category as the two checks just above, not a reachable ordinary outcome.
            var resolvedSite = await sites.GetByIdAsync(conversation.SiteId, cancellationToken);
            if (resolvedSite is null)
            {
                throw new InvalidOperationException(
                    $"Site {conversation.SiteId.Value} was not found while relaying a message to Email.");
            }

            thread = threadState;
            site = resolvedSite;
            fromAddress = EmailRecipientAddress.Build(options.Value, conversation.SiteId);
        }

        var subject = thread.Subject.StartsWith("Re:", StringComparison.OrdinalIgnoreCase)
            ? thread.Subject
            : $"Re: {thread.Subject}";

        var references = thread.RootMessageId == thread.LastInboundMessageId
            ? thread.RootMessageId
            : $"{thread.RootMessageId} {thread.LastInboundMessageId}";

        // `25-156`: the reply's own plain-text body (message.Body.Value) is carried into
        // EmailMessageToSend.Body completely unchanged from what this adapter always sent before this
        // item - only the new HtmlBody is added alongside it. EmailMimeMessageBuilder.BuildMultipartAlternative
        // (25-155) puts Body into the text/plain part verbatim and HtmlBody into the text/html part, so
        // the plain-text part a non-HTML mail client falls back to is byte-for-byte what it always was -
        // the identical "wrapper is opt-in cosmetics, never a second copy of the reply's own meaning"
        // rule 25-155 already states for its own three system emails.
        var outbound = new EmailMessageToSend(
            From: fromAddress,
            To: message.Recipient.Value,
            Subject: subject,
            Body: message.Body.Value,
            MessageId: $"<{message.MessageId.Value:D}@{options.Value.Domain}>",
            InReplyTo: thread.LastInboundMessageId,
            References: references,
            Date: clock.UtcNow,
            HtmlBody: TenantReplyEmailShell.Render(site.Name, message.Body.Value, site.WidgetConfig.PrimaryColorHex));

        var result = await client.SendAsync(outbound, cancellationToken);

        if (result.Success)
        {
            return ChannelSendOutcome.Sent(result.ProviderMessageId);
        }

        logger.LogWarning(
            "Email send refused for conversation {ConversationId}: {Reason}", message.ConversationId.Value, result.RefusalReason);
        return ChannelSendOutcome.Refused(result.RefusalReason!);
    }
}
