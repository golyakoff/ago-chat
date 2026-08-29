using Ago.Chat.Application.Abstractions;
using Ago.Chat.Domain;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Ago.Chat.Infrastructure.Vk;

/// <summary>
/// `14-08`: VK's own implementation of `14-01`'s <see cref="IInboundChannelAdapter"/> - the third real
/// adapter, modelled directly on <c>MaxChannelAdapter</c>/<c>TelegramChannelAdapter</c>. Registered as
/// itself in DI (<c>ChatModule</c>), then wrapped by <c>Ago.Chat.Module.Channels.ResilientInboundChannelAdapter</c>
/// before anything resolves <see cref="IInboundChannelAdapter"/> through the registry - this class never
/// references <c>Ago.Platform.Resilience</c> or Polly, the same discipline the port's own remarks
/// describe.
///
/// <para><b>No <c>VkLongPollingService</c>, unlike MAX's and Telegram's own hosted-service siblings.</b>
/// VK's Callback API is push-only for this item's own scope (<c>VkBotApiOptions</c>' own remarks); VK
/// does offer a separate, differently-shaped mechanism ("Bots Long Poll API",
/// <c>groups.getLongPollServer</c> plus its own polling loop) that could serve as a local-dev fallback
/// the way MAX's poller does, but it is a materially different API surface - its own provisioning call,
/// its own event envelope, its own server/key/ts bookkeeping - not a toggle on the same client this item
/// already built. Building it would be a second full adapter for one channel, not the "webhook only"
/// answer this item's own backlog note asked for once the Callback API's push shape was confirmed; it is
/// named here as a known gap rather than silently absent (see this item's own report for what that
/// leaves genuinely unverified without a real community and a real public URL).</para>
///
/// <para><b>Why this is a singleton that opens its own DI scope, rather than a scoped class.</b> Same
/// reasoning as <c>MaxChannelAdapter</c>'s own remarks: <c>InboundChannelAdapterRegistry</c> is itself a
/// singleton built from <c>IEnumerable&lt;IInboundChannelAdapter&gt;</c>, so every adapter it can ever
/// hold must be safe to keep for the process lifetime, while <see cref="IConversationRepository"/> and
/// <see cref="IChannelCredentialRepository"/> are both <c>Scoped</c> - so this class takes
/// <see cref="IServiceScopeFactory"/> and opens one scope per <see cref="SendAsync"/> call.</para>
///
/// <para><b>Resolving which tenant's community to use, and the one extra lookup MAX/Telegram never
/// needed.</b> <see cref="OutboundChannelMessage"/> carries no <c>SiteId</c>, so this class loads the
/// <see cref="Conversation"/> to find it, then looks up the site's active VK <see cref="ChannelCredential"/> -
/// identical so far. VK then needs a second value neither MAX nor Telegram do:
/// <see cref="ChannelCredential.ProviderAccountId"/>, the community's own numeric id, without which
/// <c>messages.send</c> cannot address the right community for a group access token (that field's own
/// remarks have the full reasoning). A credential missing it is not a provider refusal - every VK
/// credential this system creates populates it at registration (<c>VkChannelEndpoints</c>), so a row
/// without one is a genuine inconsistency, thrown rather than refused, the same "should not happen"
/// treatment <c>MaxChannelAdapter</c>'s own missing-conversation case gets.</para>
/// </summary>
public sealed class VkChannelAdapter(
    VkApiClient client, IServiceScopeFactory scopeFactory, ILogger<VkChannelAdapter> logger) : IInboundChannelAdapter
{
    public ChannelKind Kind => ChannelKind.Vk;

    public async Task<ChannelSendOutcome> SendAsync(OutboundChannelMessage message, CancellationToken cancellationToken)
    {
        string token;
        long groupId;
        long peerId;

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
                    $"Conversation {message.ConversationId.Value} was not found while relaying a message to VK.");
            }

            var credential = await credentials.GetActiveAsync(conversation.SiteId, ChannelKind.Vk, cancellationToken);
            if (credential is null)
            {
                const string reason =
                    "No active VK community is connected for this site - the credential was never registered, or has been revoked.";
                logger.LogWarning("VK send refused for conversation {ConversationId}: {Reason}", message.ConversationId.Value, reason);
                return ChannelSendOutcome.Refused(reason);
            }

            if (credential.ProviderAccountId is not { Length: > 0 } providerAccountId || !long.TryParse(providerAccountId, out groupId))
            {
                // Every VK credential this system creates populates ProviderAccountId at registration
                // (VkChannelEndpoints) - see this class's own remarks for why a row without one is a
                // fault, not a refusal.
                throw new InvalidOperationException(
                    $"VK channel credential {credential.Id.Value} has no usable ProviderAccountId (community id).");
            }

            if (!long.TryParse(message.Recipient.Value, out peerId))
            {
                // VkInboundMessageParser only ever constructs a numeric VK peer id - a non-numeric
                // Recipient here means this identity did not actually come from VK, a routing bug rather
                // than a provider refusal - MaxChannelAdapter's own remarks give the identical reasoning.
                var reason = $"'{message.Recipient.Value}' is not a VK peer id.";
                logger.LogWarning("VK send refused for conversation {ConversationId}: {Reason}", message.ConversationId.Value, reason);
                return ChannelSendOutcome.Refused(reason);
            }

            token = cipher.Decrypt(credential.TokenCiphertext);
        }

        // VK's own idempotency key for messages.send, derived deterministically from MessageId - a
        // retry of the same OutboundChannelMessage (this pipeline's own retries, or a redelivery) reuses
        // the identical random_id, so VK itself de-duplicates rather than posting a second copy. The
        // identical "provider gets a stable idempotency key from our own MessageId" rule resilience.md
        // states for outbound webhooks, made concrete here for a provider that, unlike MAX or Telegram,
        // actually offers one as a first-class parameter.
        var randomId = BitConverter.ToInt64(message.MessageId.Value.ToByteArray(), 0);

        var result = await client.SendMessageAsync(token, groupId, peerId, message.Body.Value, randomId, cancellationToken);

        if (result.Success)
        {
            return ChannelSendOutcome.Sent(result.ProviderMessageId);
        }

        logger.LogWarning(
            "VK send refused for conversation {ConversationId}: {Reason}", message.ConversationId.Value, result.RefusalReason);
        return ChannelSendOutcome.Refused(result.RefusalReason!);
    }
}
