using Ago.Chat.Application.Abstractions;
using Ago.Chat.Domain;
using Ago.Platform.Kernel;

namespace Ago.Chat.Application.UseCases.RevokeWebhookEndpoint;

/// <summary>
/// `6-03`'s own scope: revoke flips `Active` to `false`, never a hard delete, so delivery history for
/// this endpoint stays queryable afterward (`GetWebhookDeliveriesHandler` never filters on `Active`).
/// Idempotent the same way `DeleteAttachmentHandler` is for `Attachment.MarkDeleted` - the handler
/// checks state and short-circuits *before* calling the domain method, rather than letting
/// `WebhookEndpoint.Revoke`'s own guard surface as an error for a retried/double-clicked request.
/// </summary>
public sealed class RevokeWebhookEndpointHandler(IWebhookEndpointRepository endpoints, IPermissionChecker permissions)
{
    public async Task<Result> HandleAsync(RevokeWebhookEndpoint command, CancellationToken cancellationToken)
    {
        var allowed = await permissions.HasPermissionAsync(
            command.RequestedBy, command.SiteId, Permission.WebhookManage, cancellationToken);
        if (!allowed)
        {
            return ConversationErrors.Forbidden("Operator does not have permission to manage webhooks for this site.");
        }

        var endpoint = await endpoints.GetByIdAsync(command.WebhookEndpointId, cancellationToken);
        if (endpoint is null || endpoint.SiteId != command.SiteId)
        {
            // Same info-hiding shape DeleteAttachmentHandler already uses for a missing attachment: an
            // endpoint belonging to a different site must read identically to one that does not exist.
            return ConversationErrors.WebhookEndpointNotFound(command.WebhookEndpointId.Value);
        }

        if (!endpoint.Active)
        {
            return Result.Success();
        }

        endpoint.Revoke();
        await endpoints.SaveAsync(endpoint, cancellationToken);

        return Result.Success();
    }
}
