using Ago.Chat.Application.Abstractions;
using Ago.Chat.Domain;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Ago.Chat.Infrastructure.Avito;

/// <summary>
/// `14-11`: Avito's own implementation of `14-01`'s <see cref="IInboundChannelAdapter"/> - the fifth
/// real adapter, modelled on <c>VkChannelAdapter</c>/<c>WhatsAppChannelAdapter</c>. Registered as itself
/// in DI (<c>ChatModule</c>), then wrapped by <c>Ago.Chat.Module.Channels.ResilientInboundChannelAdapter</c>
/// before anything resolves <see cref="IInboundChannelAdapter"/> through the registry - this class never
/// references <c>Ago.Platform.Resilience</c> or Polly, the same discipline the port's own remarks
/// describe.
///
/// <para><b>No <c>AvitoLongPollingService</c></b> - Avito's own Messenger API, per this item's own
/// research (the schema names only a webhook subscribe/unsubscribe pair, no poll-for-updates endpoint),
/// offers no polling mechanism, the identical "webhook only" shape `14-08` found for VK.</para>
///
/// <para><b>Why this is a singleton that opens its own DI scope, rather than a scoped class.</b> The
/// identical reasoning <c>VkChannelAdapter</c>'s/<c>WhatsAppChannelAdapter</c>'s own remarks give: the
/// singleton <c>InboundChannelAdapterRegistry</c> is built from <c>IEnumerable&lt;IInboundChannelAdapter&gt;</c>,
/// so every adapter it holds must be safe to keep for the process lifetime, while
/// <see cref="IConversationRepository"/> and <see cref="IChannelCredentialRepository"/> are both
/// <c>Scoped</c> - so this class takes <see cref="IServiceScopeFactory"/> and opens a scope per lookup,
/// not one held for the whole call (see below for why this is now two short scopes, not one).</para>
///
/// <para><b>The chat address, not the buyer's account id, is what <see cref="OutboundChannelMessage.Recipient"/>
/// carries - the concrete answer to this item's own "listing-scoped" question.</b> Avito's Messenger API
/// addresses a send by <c>chat_id</c> (<c>POST /messenger/v1/accounts/{user_id}/chats/{chat_id}/messages</c>),
/// not by the buyer's own account id, and a buyer who has messaged this seller about two different
/// listings holds two distinct <c>chat_id</c>s for the identical Avito account. <see cref="ChannelIdentity"/>
/// keys on <see cref="ExternalChannelAddress"/> alone, so using <c>chat_id</c> as that address means each
/// listing-scoped conversation becomes its own AGO <c>Visitor</c>/<c>Conversation</c> - implicitly
/// preserving Avito's own listing dimension without <c>item_id</c> (or the word "listing") ever appearing
/// anywhere in this system's vocabulary (<see cref="AvitoInboundMessageParser"/>'s own remarks, and this
/// item's own report, have the fuller reasoning). <b>The concrete scenario that ruled out the buyer's
/// account id instead:</b> if <see cref="ExternalChannelAddress"/> were the buyer's own numeric Avito
/// account id, every listing a buyer ever contacted this seller about would collapse into a single AGO
/// conversation, and a reply typed there would have no way to know which of the buyer's several live
/// Avito <c>chat_id</c>s to actually deliver to - Avito's own send endpoint takes exactly one
/// <c>chat_id</c>, not an account id. Using the account id would not merely lose a display nicety; it
/// would break outbound delivery for the second and every subsequent listing a buyer ever asks about. The
/// real cost of the chosen shape, named plainly rather than left implicit: a buyer who contacts this
/// seller about two listings shows up in AGO Chat's console as two separate, unlinked "visitors" - the
/// same accepted limitation this codebase already has for any provider whose own identity concept does
/// not perfectly match AGO's own <see cref="Visitor"/> (`adr/0055`).</para>
///
/// <para><b>Resolving which tenant's Avito account to use, and the value neither MAX nor Telegram
/// needed.</b> <see cref="OutboundChannelMessage"/> carries no <c>SiteId</c>, so this class loads the
/// <see cref="Conversation"/> to find it, then looks up the site's active Avito
/// <see cref="ChannelCredential"/> - identical so far to VK/WhatsApp. Avito then needs
/// <see cref="ChannelCredential.ProviderAccountId"/>, the seller's own numeric Avito user id (discovered
/// once at connect time via <see cref="AvitoApiClient.GetSelfAsync"/>), because
/// <c>POST /messenger/v1/accounts/{user_id}/...</c> is addressed by it directly - the identical
/// "self-addressing token is not enough on its own" shape VK's <c>group_id</c>/WhatsApp's
/// <c>phone_number_id</c> already established. A credential missing it is thrown rather than refused, the
/// same "should not happen" treatment every precedent in this stage gives.</para>
///
/// <para><b>Reactive OAuth refresh on 401 - the one mechanism with no precedent anywhere else in this
/// stage.</b> Avito's own access token expires every 24 hours
/// (<see cref="Domain.ChannelCredential.RefreshTokenCiphertext"/>'s own remarks); a real deployment
/// would see this path exercised routinely, not as an edge case. Rather than a background job that
/// proactively refreshes tokens nobody is about to use (the same kind of premature-generalization
/// CLAUDE.md warns against, and a second moving part this item's own scope did not need), this class
/// treats an expired token as an ordinary, bounded, single-retry recovery: a
/// <see cref="AvitoAccessTokenExpiredException"/> from the first send attempt triggers exactly one
/// refresh-and-retry, using the credential's own stored <see cref="Domain.ChannelCredential.RefreshTokenCiphertext"/>
/// and this deployment's own <see cref="AvitoApiOptions.ClientId"/>/<see cref="AvitoApiOptions.ClientSecret"/>
/// (`AvitoApiOptions`'s own remarks on why those are AGO's application credentials, not a per-tenant
/// value). Avito rotates the refresh token on every use (confirmed from the schema's own refresh-response
/// example), so both the new access token and the new refresh token are persisted back
/// (<see cref="Domain.ChannelCredential.RotateOAuthTokens"/>) before the retry - failing to persist the
/// rotated refresh token would silently strand the credential the next time a refresh is needed. A
/// refresh that itself fails (no stored refresh token, or Avito rejects it - the refresh token's own
/// lifetime is undocumented anywhere this item's research reached) surfaces as an ordinary
/// <see cref="ChannelSendOutcome.Refused"/> naming that the channel needs to be reconnected, never a
/// silent retry loop.</para>
/// </summary>
public sealed class AvitoChannelAdapter(
    AvitoApiClient client, IOptions<AvitoApiOptions> options, IServiceScopeFactory scopeFactory,
    ILogger<AvitoChannelAdapter> logger) : IInboundChannelAdapter
{
    public ChannelKind Kind => ChannelKind.Avito;

    public async Task<ChannelSendOutcome> SendAsync(OutboundChannelMessage message, CancellationToken cancellationToken)
    {
        var loaded = await LoadCredentialAsync(message.ConversationId, cancellationToken);
        if (loaded is null)
        {
            const string reason =
                "No active Avito account is connected for this site - the credential was never registered, or has been revoked.";
            logger.LogWarning("Avito send refused for conversation {ConversationId}: {Reason}", message.ConversationId.Value, reason);
            return ChannelSendOutcome.Refused(reason);
        }

        var (credentialId, userId, accessToken, refreshToken) = loaded.Value;
        var chatId = message.Recipient.Value;

        try
        {
            var result = await client.SendMessageAsync(accessToken, userId, chatId, message.Body.Value, cancellationToken);
            return ToOutcome(result);
        }
        catch (AvitoAccessTokenExpiredException)
        {
            if (refreshToken is not { Length: > 0 })
            {
                const string reason = "Avito's access token expired and no refresh token is stored for this credential - reconnect this channel.";
                logger.LogWarning("Avito send refused for conversation {ConversationId}: {Reason}", message.ConversationId.Value, reason);
                return ChannelSendOutcome.Refused(reason);
            }

            string refreshedAccessToken;
            try
            {
                refreshedAccessToken = await RefreshAndPersistAsync(credentialId, refreshToken, cancellationToken);
            }
            catch (AvitoApiCallException ex)
            {
                var reason = $"Avito refused to refresh this channel's access token - reconnect this channel. ({ex.Message})";
                logger.LogWarning("Avito send refused for conversation {ConversationId}: {Reason}", message.ConversationId.Value, reason);
                return ChannelSendOutcome.Refused(reason);
            }

            try
            {
                var retried = await client.SendMessageAsync(refreshedAccessToken, userId, chatId, message.Body.Value, cancellationToken);
                return ToOutcome(retried);
            }
            catch (AvitoAccessTokenExpiredException)
            {
                // Refreshed once, immediately rejected again - not the ordinary "token aged past 24
                // hours" case this method exists for. Refused rather than retried a second time, the
                // same bounded-retry discipline every precedent in this stage applies to its own
                // recovery path.
                const string reason = "Avito rejected the freshly refreshed access token - reconnect this channel.";
                logger.LogWarning("Avito send refused for conversation {ConversationId}: {Reason}", message.ConversationId.Value, reason);
                return ChannelSendOutcome.Refused(reason);
            }
        }
    }

    private async Task<string> RefreshAndPersistAsync(ChannelCredentialId credentialId, string refreshToken, CancellationToken cancellationToken)
    {
        var refreshed = await client.RefreshAccessTokenAsync(
            options.Value.ClientId, options.Value.ClientSecret, refreshToken, cancellationToken);

        await using var scope = scopeFactory.CreateAsyncScope();
        var credentials = scope.ServiceProvider.GetRequiredService<IChannelCredentialRepository>();
        var cipher = scope.ServiceProvider.GetRequiredService<IChannelCredentialCipher>();

        var credential = await credentials.GetByIdAsync(credentialId, cancellationToken);
        if (credential is null)
        {
            // Should not happen: this same credential was loaded moments ago by this very call -
            // MaxChannelAdapter's own remarks explain why this is thrown rather than refused.
            throw new InvalidOperationException($"Channel credential {credentialId.Value} disappeared mid-refresh.");
        }

        credential.RotateOAuthTokens(
            cipher.Encrypt(refreshed.AccessToken!), cipher.Encrypt(refreshed.RefreshToken!));
        await credentials.SaveAsync(credential, cancellationToken);

        return refreshed.AccessToken!;
    }

    private async Task<(ChannelCredentialId CredentialId, long UserId, string AccessToken, string? RefreshToken)?> LoadCredentialAsync(
        ConversationId conversationId, CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var conversations = scope.ServiceProvider.GetRequiredService<IConversationRepository>();
        var credentials = scope.ServiceProvider.GetRequiredService<IChannelCredentialRepository>();
        var cipher = scope.ServiceProvider.GetRequiredService<IChannelCredentialCipher>();

        var conversation = await conversations.GetByIdAsync(conversationId, cancellationToken);
        if (conversation is null)
        {
            // Should not happen: DeliverChannelMessageHandler just loaded this same conversation to
            // build the message it handed to this adapter - MaxChannelAdapter's own remarks explain why
            // this is thrown rather than refused.
            throw new InvalidOperationException($"Conversation {conversationId.Value} was not found while relaying a message to Avito.");
        }

        var credential = await credentials.GetActiveAsync(conversation.SiteId, ChannelKind.Avito, cancellationToken);
        if (credential is null)
        {
            return null;
        }

        if (credential.ProviderAccountId is not { Length: > 0 } providerAccountId || !long.TryParse(providerAccountId, out var userId))
        {
            // Every Avito credential this system creates populates ProviderAccountId at registration
            // (AvitoChannelEndpoints) - see this class's own remarks for why a row without one is a
            // fault, not a refusal.
            throw new InvalidOperationException(
                $"Avito channel credential {credential.Id.Value} has no usable ProviderAccountId (Avito user id).");
        }

        var accessToken = cipher.Decrypt(credential.TokenCiphertext);
        var refreshToken = credential.RefreshTokenCiphertext is { } refreshCiphertext ? cipher.Decrypt(refreshCiphertext) : null;

        return (credential.Id, userId, accessToken, refreshToken);
    }

    private static ChannelSendOutcome ToOutcome(AvitoSendResult result) =>
        result.Success ? ChannelSendOutcome.Sent(result.ProviderMessageId) : ChannelSendOutcome.Refused(result.RefusalReason!);
}
