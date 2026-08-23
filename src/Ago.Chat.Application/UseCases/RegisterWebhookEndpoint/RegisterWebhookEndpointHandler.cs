using Ago.Chat.Application.Abstractions;
using Ago.Chat.Domain;
using Ago.Platform.Kernel;

namespace Ago.Chat.Application.UseCases.RegisterWebhookEndpoint;

/// <summary>
/// `6-03`: registration is the one place the plaintext secret exists outside the caller's own
/// storage - generated here, encrypted here, returned here, and never persisted or logged in plaintext
/// (`WebhookEndpoint`'s own remarks on why this is a reversible cipher, not a hash).
/// </summary>
public sealed class RegisterWebhookEndpointHandler(
    IWebhookEndpointRepository endpoints,
    IPermissionChecker permissions,
    IWebhookSecretGenerator secretGenerator,
    IWebhookSecretCipher secretCipher,
    IIdGenerator idGenerator,
    IClock clock)
{
    public async Task<Result<RegisteredWebhookEndpoint>> HandleAsync(
        RegisterWebhookEndpoint command, CancellationToken cancellationToken)
    {
        var allowed = await permissions.HasPermissionAsync(
            command.RequestedBy, command.SiteId, Permission.WebhookManage, cancellationToken);
        if (!allowed)
        {
            return ConversationErrors.Forbidden("Operator does not have permission to manage webhooks for this site.");
        }

        var validationFailure = WebhookUrlValidator.Validate(command.Url);
        if (validationFailure is not null)
        {
            return ConversationErrors.WebhookInvalidUrl(validationFailure);
        }

        var now = clock.UtcNow;
        var id = new WebhookEndpointId(idGenerator.NewId(now));

        var secret = secretGenerator.NewSecret();
        var ciphertext = secretCipher.Encrypt(secret);

        var endpoint = WebhookEndpoint.Register(id, command.SiteId, command.Url, ciphertext, now);
        await endpoints.SaveAsync(endpoint, cancellationToken);

        return new RegisteredWebhookEndpoint(id.Value, secret, command.Url, now);
    }
}
