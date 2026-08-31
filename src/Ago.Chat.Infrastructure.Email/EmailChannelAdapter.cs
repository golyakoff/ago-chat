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
/// configuration, not a per-site secret, so the only site-specific fact this method needs -
/// <see cref="Domain.SiteId"/>, to build the site's own <c>support+{siteId}@{domain}</c> sender address
/// (<see cref="EmailRecipientAddress"/>'s own remarks) - comes from loading the <see cref="Conversation"/>
/// alone, the same lookup <c>WhatsAppChannelAdapter</c>'s own remarks describe needing for the identical
/// reason (<see cref="OutboundChannelMessage"/> carries no <c>SiteId</c> of its own).</para>
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

        await using (var scope = scopeFactory.CreateAsyncScope())
        {
            var conversations = scope.ServiceProvider.GetRequiredService<IConversationRepository>();
            var threads = scope.ServiceProvider.GetRequiredService<IEmailThreadStore>();

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

            thread = threadState;
            fromAddress = EmailRecipientAddress.Build(options.Value, conversation.SiteId);
        }

        var subject = thread.Subject.StartsWith("Re:", StringComparison.OrdinalIgnoreCase)
            ? thread.Subject
            : $"Re: {thread.Subject}";

        var references = thread.RootMessageId == thread.LastInboundMessageId
            ? thread.RootMessageId
            : $"{thread.RootMessageId} {thread.LastInboundMessageId}";

        var outbound = new EmailMessageToSend(
            From: fromAddress,
            To: message.Recipient.Value,
            Subject: subject,
            Body: message.Body.Value,
            MessageId: $"<{message.MessageId.Value:D}@{options.Value.Domain}>",
            InReplyTo: thread.LastInboundMessageId,
            References: references,
            Date: clock.UtcNow);

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
