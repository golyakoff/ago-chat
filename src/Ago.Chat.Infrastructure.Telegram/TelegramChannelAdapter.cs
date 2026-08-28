using Ago.Chat.Application.Abstractions;
using Ago.Chat.Domain;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Ago.Chat.Infrastructure.Telegram;

/// <summary>
/// `14-07`: Telegram's real implementation of `14-01`'s <see cref="IInboundChannelAdapter"/> - the same
/// shape as <c>Ago.Chat.Infrastructure.MaxBot.MaxChannelAdapter</c>, which this class was modelled on
/// directly (both being the second and later proofs that the one port genuinely serves more than one
/// provider). Registered as itself in DI (<c>ChatModule</c>), then wrapped by
/// <c>Ago.Chat.Module.Channels.ResilientInboundChannelAdapter</c> before anything resolves
/// <see cref="IInboundChannelAdapter"/> through the registry - this class never references
/// <c>Ago.Platform.Resilience</c> or Polly, the identical "an implementation is written as if the
/// provider always answers" discipline the port's own remarks describe.
///
/// <para><b>Why this is a singleton that opens its own DI scope, rather than a scoped class.</b> Same
/// reasoning as <c>MaxChannelAdapter</c>'s own remarks: <c>InboundChannelAdapterRegistry</c> is itself a
/// singleton built from <c>IEnumerable&lt;IInboundChannelAdapter&gt;</c>, so every adapter it can ever
/// hold must be safe to keep for the process lifetime, while <see cref="IConversationRepository"/> and
/// <see cref="IChannelCredentialRepository"/> are both <c>Scoped</c> - so this class takes
/// <see cref="IServiceScopeFactory"/> and opens one scope per <see cref="SendAsync"/> call.</para>
///
/// <para><b>Resolving which tenant's bot to use.</b> Identical to MAX: <see cref="OutboundChannelMessage"/>
/// carries no <c>SiteId</c>, so this class loads the <see cref="Conversation"/> to find it, then looks
/// up the site's active Telegram <see cref="ChannelCredential"/>. A missing credential is a
/// <em>terminal</em> outcome, not a fault - `adr/0069`'s "surfaces as a rejected call at use time",
/// mapped to <see cref="ChannelSendOutcome.Refused"/> so it is never retried.</para>
/// </summary>
public sealed class TelegramChannelAdapter(
    TelegramApiClient client, IServiceScopeFactory scopeFactory, ILogger<TelegramChannelAdapter> logger)
    : IInboundChannelAdapter
{
    public ChannelKind Kind => ChannelKind.Telegram;

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
                // build the message it handed to this adapter - see MaxChannelAdapter's own remarks on
                // why this is thrown rather than refused.
                throw new InvalidOperationException(
                    $"Conversation {message.ConversationId.Value} was not found while relaying a message to Telegram.");
            }

            var credential = await credentials.GetActiveAsync(conversation.SiteId, ChannelKind.Telegram, cancellationToken);
            if (credential is null)
            {
                const string reason =
                    "No active Telegram bot is connected for this site - the credential was never registered, or has been revoked.";
                logger.LogWarning("Telegram send refused for conversation {ConversationId}: {Reason}", message.ConversationId.Value, reason);
                return ChannelSendOutcome.Refused(reason);
            }

            if (!long.TryParse(message.Recipient.Value, out chatId))
            {
                // TelegramInboundMessageParser only ever constructs a numeric Telegram chat id - a
                // non-numeric Recipient here means this identity did not actually come from Telegram, a
                // routing bug rather than a provider refusal - see MaxChannelAdapter's own remarks for
                // the identical reasoning.
                var reason = $"'{message.Recipient.Value}' is not a Telegram chat id.";
                logger.LogWarning("Telegram send refused for conversation {ConversationId}: {Reason}", message.ConversationId.Value, reason);
                return ChannelSendOutcome.Refused(reason);
            }

            token = cipher.Decrypt(credential.TokenCiphertext);
        }

        var result = await client.SendMessageAsync(token, chatId, message.Body.Value, cancellationToken);

        if (result.Success)
        {
            return ChannelSendOutcome.Sent(result.ProviderMessageId);
        }

        logger.LogWarning(
            "Telegram send refused for conversation {ConversationId}: {Reason}", message.ConversationId.Value, result.RefusalReason);
        return ChannelSendOutcome.Refused(result.RefusalReason!);
    }
}
