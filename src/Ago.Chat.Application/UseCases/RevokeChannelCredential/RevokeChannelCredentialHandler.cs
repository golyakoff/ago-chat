using Ago.Chat.Application.Abstractions;
using Ago.Platform.Kernel;

namespace Ago.Chat.Application.UseCases.RevokeChannelCredential;

/// <summary>
/// `14-02`/`adr/0069`: flips `Active` to `false`, never a hard delete - `RevokeWebhookEndpointHandler`'s
/// own shape, including the same idempotent short-circuit for a retried/double-clicked request. Erasing
/// the row entirely is a separate, later concern (`16-02`, tenant offboarding) - see
/// `Domain.ChannelCredential.Revoke`'s own remarks.
/// </summary>
public sealed class RevokeChannelCredentialHandler(IChannelCredentialRepository credentials, IPermissionChecker permissions)
{
    public async Task<Result> HandleAsync(RevokeChannelCredential command, CancellationToken cancellationToken)
    {
        var allowed = await permissions.HasPermissionAsync(
            command.RequestedBy, command.SiteId, Domain.Permission.ChannelManage, cancellationToken);
        if (!allowed)
        {
            return ConversationErrors.Forbidden("Operator does not have permission to manage channels for this site.");
        }

        var credential = await credentials.GetByIdAsync(command.ChannelCredentialId, cancellationToken);
        if (credential is null || credential.SiteId != command.SiteId)
        {
            return ConversationErrors.ChannelCredentialNotFound(command.ChannelCredentialId.Value);
        }

        if (!credential.Active)
        {
            return Result.Success();
        }

        credential.Revoke();
        await credentials.SaveAsync(credential, cancellationToken);

        return Result.Success();
    }
}
