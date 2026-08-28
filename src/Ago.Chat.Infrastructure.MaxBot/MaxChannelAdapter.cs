using Ago.Chat.Application.Abstractions;
using Ago.Chat.Domain;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Ago.Chat.Infrastructure.MaxBot;

/// <summary>
/// `14-02`: the first real implementation of `14-01`'s <see cref="IInboundChannelAdapter"/> - the proof
/// the port is shaped correctly (this item's own Goal). Registered as itself in DI
/// (<c>ChatModule</c>), then wrapped by <c>Ago.Chat.Module.Channels.ResilientInboundChannelAdapter</c>
/// before anything resolves <see cref="IInboundChannelAdapter"/> through the registry - this class never
/// references <c>Ago.Platform.Resilience</c> or Polly, the same "an implementation is written as if the
/// provider always answers" discipline the port's own remarks describe.
///
/// <para><b>Why this is a singleton that opens its own DI scope, rather than a scoped class.</b>
/// <c>InboundChannelAdapterRegistry</c> is itself a singleton (constructed once, from
/// <c>IEnumerable&lt;IInboundChannelAdapter&gt;</c>, so every channel adapter it can ever resolve must be
/// safe to hold for the process lifetime - the standard ASP.NET Core rule that a singleton may not
/// capture a scoped dependency through its constructor. <see cref="IConversationRepository"/> and
/// <see cref="IChannelCredentialRepository"/> are both <c>Scoped</c> (they share one <c>DbContext</c> per
/// unit of work), so this class takes <see cref="IServiceScopeFactory"/> instead and opens one scope per
/// <see cref="SendAsync"/> call - <c>MaxLongPollingService</c>'s and <c>OfflineAutoReplyConsumer</c>'s
/// own precedent for "a singleton driving scoped work."</para>
///
/// <para><b>Resolving which tenant's bot to use.</b> <see cref="OutboundChannelMessage"/> carries no
/// <c>SiteId</c> (`14-01`'s own shape - see that record's remarks), so this class loads the
/// <see cref="Conversation"/> to find it, then looks up the site's active MAX
/// <see cref="ChannelCredential"/>. A missing credential (never connected, or revoked since the visitor
/// last wrote in) is a <em>terminal</em> outcome, not a fault: `adr/0069`'s own reasoning is that
/// revocation "surfaces as a rejected call at use time," and this is exactly that surfacing, mapped to
/// <see cref="ChannelSendOutcome.Refused"/> rather than an exception so it is never retried.</para>
/// </summary>
public sealed class MaxChannelAdapter(
    MaxApiClient client, IServiceScopeFactory scopeFactory, ILogger<MaxChannelAdapter> logger) : IInboundChannelAdapter
{
    public ChannelKind Kind => ChannelKind.Max;

    public async Task<ChannelSendOutcome> SendAsync(OutboundChannelMessage message, CancellationToken cancellationToken)
    {
        string token;
        long chatId;

        await using (var scope = scopeFactory.CreateAsyncScope())
        {
            var conversations = scope.ServiceProvider.GetRequiredService<IConversationRepository>();
            var credentials = scope.ServiceProvider.GetRequiredService<IChannelCredentialRepository>();
            var cipher = scope.ServiceProvider.GetRequiredService<IChannelCredentialCipher>();

            var conversation = await conversations.GetByIdAsync(message.ConversationId, cancellationToken);
            if (conversation is null)
            {
                // Should not happen: DeliverChannelMessageHandler just loaded this same conversation to
                // build the message it handed to this adapter. Thrown, not refused - an inconsistency
                // this deep is a fault the resilience pipeline's retry gives a chance to self-heal (a
                // replica reading a not-yet-visible row), not a provider's own considered refusal.
                throw new InvalidOperationException(
                    $"Conversation {message.ConversationId.Value} was not found while relaying a message to MAX.");
            }

            var credential = await credentials.GetActiveAsync(conversation.SiteId, ChannelKind.Max, cancellationToken);
            if (credential is null)
            {
                const string reason =
                    "No active MAX bot is connected for this site - the credential was never registered, or has been revoked.";
                logger.LogWarning("MAX send refused for conversation {ConversationId}: {Reason}", message.ConversationId.Value, reason);
                return ChannelSendOutcome.Refused(reason);
            }

            if (!long.TryParse(message.Recipient.Value, out chatId))
            {
                // MaxInboundMessageParser only ever constructs a numeric MAX chat id - a non-numeric
                // Recipient here means this identity did not actually come from MAX, a routing bug
                // rather than a provider refusal, so this is refused rather than thrown (retrying would
                // produce the identical outcome forever - the retry-worthiness test resilience.md
                // applies).
                var reason = $"'{message.Recipient.Value}' is not a MAX chat id.";
                logger.LogWarning("MAX send refused for conversation {ConversationId}: {Reason}", message.ConversationId.Value, reason);
                return ChannelSendOutcome.Refused(reason);
            }

            token = cipher.Decrypt(credential.TokenCiphertext);
        }

        var result = await client.SendMessageAsync(token, chatId, message.Body.Value, cancellationToken);

        if (result.Success)
        {
            return ChannelSendOutcome.Sent(result.ProviderMessageId);
        }

        // Found live, 2026-08-28: a provider refusal's own reason (MAX's real response body, not just
        // its status code) reached nowhere before this - DeliverChannelMessageHandler discards
        // ChannelSendOutcome down to a bare Delivered/Refused enum, so the one place this string is
        // ever seen has to be here, at the point it is still attached to which conversation it was
        // trying to reach. Warning, not error - a provider refusal is an expected outcome this system
        // already models (adr/0069's own "surfaces as a rejected call at use time"), not a fault.
        logger.LogWarning(
            "MAX send refused for conversation {ConversationId}: {Reason}", message.ConversationId.Value, result.RefusalReason);
        return ChannelSendOutcome.Refused(result.RefusalReason!);
    }
}
