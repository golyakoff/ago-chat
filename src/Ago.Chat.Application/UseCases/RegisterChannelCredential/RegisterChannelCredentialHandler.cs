using System.Security.Cryptography;
using System.Text;
using Ago.Chat.Application.Abstractions;
using Ago.Chat.Domain;
using Ago.Platform.Kernel;

namespace Ago.Chat.Application.UseCases.RegisterChannelCredential;

/// <summary>
/// `14-02`/`adr/0069`: registration is the one place the shop's plaintext token exists outside its own
/// records - encrypted here, persisted here, and never returned to any caller in decrypted form
/// (`Domain.ChannelCredential`'s own remarks on why this differs from
/// `RegisterWebhookEndpointHandler`'s own plaintext-once-in-the-response shape).
///
/// <para><b>Channel-neutral on purpose.</b> This handler never calls MAX, or any other provider's own
/// subscribe API - that call is provider-shaped (a URL, an update-types list, MAX's own JSON), which
/// `adr/0006`'s "largest common denominator that does not lie" keeps below the Infrastructure boundary.
/// <c>Ago.Chat.Api</c>'s MAX-specific endpoint calls this handler first, then uses the returned
/// <see cref="RegisteredChannelCredential.WebhookSecret"/> to complete MAX's own <c>POST /subscriptions</c>
/// call, and revokes the credential this handler just created if that call comes back a clear rejection
/// - see that endpoint's own remarks for the full sequence and why it could not be done the other way
/// around (MAX must be told the secret to expect, and the secret cannot exist before this handler
/// generates it).</para>
/// </summary>
public sealed class RegisterChannelCredentialHandler(
    IChannelCredentialRepository credentials,
    IPermissionChecker permissions,
    IChannelCredentialCipher cipher,
    IWebhookSecretGenerator secretGenerator,
    IIdGenerator idGenerator,
    IClock clock)
{
    public async Task<Result<RegisteredChannelCredential>> HandleAsync(
        RegisterChannelCredential command, CancellationToken cancellationToken)
    {
        var allowed = await permissions.HasPermissionAsync(
            command.RequestedBy, command.SiteId, Permission.ChannelManage, cancellationToken);
        if (!allowed)
        {
            return ConversationErrors.Forbidden("Operator does not have permission to manage channels for this site.");
        }

        var validationFailure = ChannelCredentialTokenValidator.Validate(command.Token);
        if (validationFailure is not null)
        {
            return ConversationErrors.ChannelInvalidToken(validationFailure);
        }

        var existing = await credentials.GetActiveAsync(command.SiteId, command.Kind, cancellationToken);
        if (existing is not null)
        {
            return ConversationErrors.ChannelAlreadyConnected(
                $"Site {command.SiteId.Value} already has an active {command.Kind} credential. Revoke it before registering a new one.");
        }

        var now = clock.UtcNow;
        var id = new ChannelCredentialId(idGenerator.NewId(now));

        var tokenCiphertext = cipher.Encrypt(command.Token.Trim());
        var webhookSecret = secretGenerator.NewSecret();
        var webhookSecretHash = SHA256.HashData(Encoding.UTF8.GetBytes(webhookSecret));

        var credential = Domain.ChannelCredential.Register(
            id, command.SiteId, command.Kind, tokenCiphertext, webhookSecretHash, now);
        await credentials.SaveAsync(credential, cancellationToken);

        return new RegisteredChannelCredential(id, command.Kind, webhookSecret, now);
    }
}
