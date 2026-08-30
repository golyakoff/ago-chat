using Ago.Chat.Application.Abstractions;
using Ago.Chat.Domain;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Ago.Chat.Infrastructure.WhatsApp;

/// <summary>
/// `14-10`: WhatsApp's own implementation of `14-01`'s <see cref="IInboundChannelAdapter"/> - the fourth
/// real adapter, modelled on <c>MaxChannelAdapter</c>/<c>TelegramChannelAdapter</c>/<c>VkChannelAdapter</c>.
/// Registered as itself in DI (<c>ChatModule</c>), then wrapped by
/// <c>Ago.Chat.Module.Channels.ResilientInboundChannelAdapter</c> before anything resolves
/// <see cref="IInboundChannelAdapter"/> through the registry - this class never references
/// <c>Ago.Platform.Resilience</c> or Polly, the same discipline the port's own remarks describe.
///
/// <para><b>No <c>WhatsAppLongPollingService</c> - Meta's Cloud API offers no polling mechanism at all,
/// unlike MAX's/Telegram's own dual shape and even VK's own (rejected) long-poll alternative.</b> This
/// channel is webhook-only by construction, not by this item's own choice - VK's own "webhook only"
/// scope decision was a choice among two real options; WhatsApp's is not a choice at all.</para>
///
/// <para><b>Why this is a singleton that opens its own DI scope, rather than a scoped class.</b> The
/// identical reasoning <c>MaxChannelAdapter</c>'s/<c>VkChannelAdapter</c>'s own remarks give: the
/// singleton <c>InboundChannelAdapterRegistry</c> is built from <c>IEnumerable&lt;IInboundChannelAdapter&gt;</c>,
/// so every adapter it holds must be safe to keep for the process lifetime, while
/// <see cref="IConversationRepository"/> and <see cref="IChannelCredentialRepository"/> are both
/// <c>Scoped</c> - so this class takes <see cref="IServiceScopeFactory"/> and opens one scope per
/// <see cref="SendAsync"/> call.</para>
///
/// <para><b>The 24-hour customer-service-window constraint, and the scope decision this item's own
/// backlog note asked for made explicitly.</b> Meta's Cloud API refuses a free-form reply sent more than
/// 24 hours after the visitor's own last inbound message (error 131047 - <see cref="WhatsAppApiClient"/>'s
/// own remarks) unless it is a pre-approved message template. AGO Chat's own target use case for this
/// item - an operator answering a visitor who just messaged, through the same console queue every other
/// channel already uses - is squarely inside that window on every ordinary path: the window opens the
/// instant <c>ReceiveChannelMessageHandler</c> records the visitor's own inbound message, and an
/// operator's reply through this adapter follows within the same conversation, not after a day of
/// silence. <b>Message-template support is out of scope for this item</b>, deliberately, not silently -
/// building it would mean a template-registration flow with Meta (a whole approval process this system
/// does not control), a template-selection UI no other channel needs, and variable-substitution
/// machinery for a case this product's own use case does not reach - exactly the premature
/// generalization CLAUDE.md warns against building speculatively. What this class does instead: a
/// 131047 refusal is neither retried nor silently swallowed - it surfaces as an ordinary
/// <see cref="ChannelSendOutcome.Refused"/> with a reason that names the real constraint, so an operator
/// sees "WhatsApp requires a pre-approved message template for a reply sent more than 24 hours after the
/// visitor's last message" rather than a generic failure or an infinite retry loop. The constraint is
/// respected, not ignored; only the machinery to work around it is the thing left unbuilt.</para>
///
/// <para><b>Resolving which tenant's number to use - the identical extra lookup <c>VkChannelAdapter</c>'s
/// own remarks describe for VK's <c>group_id</c>.</b> <see cref="OutboundChannelMessage"/> carries no
/// <c>SiteId</c>, so this class loads the <see cref="Conversation"/> to find it, then looks up the site's
/// active WhatsApp <see cref="ChannelCredential"/>. WhatsApp needs the identical second value VK does:
/// <see cref="ChannelCredential.ProviderAccountId"/>, here the number's own <c>phone_number_id</c>,
/// without which the Cloud API's <c>/messages</c> endpoint has no path to post to. A credential missing
/// it is not a provider refusal - every WhatsApp credential this system creates populates it at
/// registration (<c>WhatsAppChannelEndpoints</c>), so a row without one is a genuine inconsistency,
/// thrown rather than refused, the same "should not happen" treatment <c>MaxChannelAdapter</c>'s/
/// <c>VkChannelAdapter</c>'s own missing-conversation cases get.</para>
///
/// <para><b>No idempotency key on the outbound call - a real, named gap, not an oversight.</b> VK's own
/// <c>random_id</c> lets a resilience-pipeline retry reach VK with the identical value, so VK's own
/// deduplication absorbs a retried send. Meta's Cloud API <c>/messages</c> endpoint, per this item's own
/// research, offers no equivalent per-call idempotency parameter - the same gap MAX's and Telegram's own
/// outbound clients already carry (neither offers one either), so this is not a WhatsApp-specific
/// regression, but it is worth stating plainly: a resilience-pipeline retry after a transient fault could,
/// in principle, produce a visible duplicate reply on the recipient's device, exactly as it already can
/// for MAX and Telegram.</para>
/// </summary>
public sealed class WhatsAppChannelAdapter(
    WhatsAppApiClient client, IServiceScopeFactory scopeFactory, ILogger<WhatsAppChannelAdapter> logger) : IInboundChannelAdapter
{
    public ChannelKind Kind => ChannelKind.WhatsApp;

    public async Task<ChannelSendOutcome> SendAsync(OutboundChannelMessage message, CancellationToken cancellationToken)
    {
        string token;
        string phoneNumberId;

        await using (var scope = scopeFactory.CreateAsyncScope())
        {
            var conversations = scope.ServiceProvider.GetRequiredService<IConversationRepository>();
            var credentials = scope.ServiceProvider.GetRequiredService<IChannelCredentialRepository>();
            var cipher = scope.ServiceProvider.GetRequiredService<IChannelCredentialCipher>();

            var conversation = await conversations.GetByIdAsync(message.ConversationId, cancellationToken);
            if (conversation is null)
            {
                // Should not happen: DeliverChannelMessageHandler just loaded this same conversation to
                // build the message it handed to this adapter - MaxChannelAdapter's own remarks explain
                // why this is thrown rather than refused.
                throw new InvalidOperationException(
                    $"Conversation {message.ConversationId.Value} was not found while relaying a message to WhatsApp.");
            }

            var credential = await credentials.GetActiveAsync(conversation.SiteId, ChannelKind.WhatsApp, cancellationToken);
            if (credential is null)
            {
                const string reason =
                    "No active WhatsApp number is connected for this site - the credential was never registered, or has been revoked.";
                logger.LogWarning("WhatsApp send refused for conversation {ConversationId}: {Reason}", message.ConversationId.Value, reason);
                return ChannelSendOutcome.Refused(reason);
            }

            if (credential.ProviderAccountId is not { Length: > 0 } accountId)
            {
                // Every WhatsApp credential this system creates populates ProviderAccountId at
                // registration (WhatsAppChannelEndpoints) - see this class's own remarks for why a row
                // without one is a fault, not a refusal.
                throw new InvalidOperationException(
                    $"WhatsApp channel credential {credential.Id.Value} has no usable ProviderAccountId (phone_number_id).");
            }

            phoneNumberId = accountId;

            // No non-numeric-recipient check the way VkChannelAdapter's own equivalent has - VK's
            // messages.send needs a genuinely numeric peer_id, so a non-numeric Recipient is a real,
            // reachable routing bug there. WhatsApp's `to` field is an ordinary phone-number string with
            // no such constraint, and ExternalChannelAddress's own constructor already refuses an empty
            // value at construction time (Domain.ExternalChannelAddress's own remarks) - so there is no
            // reachable "empty recipient" state left for this class to guard against a second time.

            token = cipher.Decrypt(credential.TokenCiphertext);
        }

        var result = await client.SendMessageAsync(token, phoneNumberId, message.Recipient.Value, message.Body.Value, cancellationToken);

        if (result.Success)
        {
            return ChannelSendOutcome.Sent(result.ProviderMessageId);
        }

        logger.LogWarning(
            "WhatsApp send refused for conversation {ConversationId}: {Reason}", message.ConversationId.Value, result.RefusalReason);
        return ChannelSendOutcome.Refused(result.RefusalReason!);
    }
}
