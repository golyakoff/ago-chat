using Ago.Chat.Application.Abstractions;
using Ago.Chat.Contracts;
using Ago.Chat.Domain;
using Ago.Platform.Kernel;

namespace Ago.Chat.Application.UseCases.ListWebhookEndpoints;

public sealed class ListWebhookEndpointsHandler(IWebhookEndpointRepository endpoints, IPermissionChecker permissions)
{
    public async Task<Result<WebhookEndpointsResponse>> HandleAsync(
        ListWebhookEndpoints query, CancellationToken cancellationToken)
    {
        var allowed = await permissions.HasPermissionAsync(
            query.RequestedBy, query.SiteId, Permission.WebhookManage, cancellationToken);
        if (!allowed)
        {
            return ConversationErrors.Forbidden("Operator does not have permission to manage webhooks for this site.");
        }

        var siteEndpoints = await endpoints.GetAllForSiteAsync(query.SiteId, cancellationToken);

        return new WebhookEndpointsResponse(siteEndpoints.Select(ToDto).ToList());
    }

    // No secret field mapped, ever - WebhookEndpoint.SecretCiphertext never reaches this DTO
    // (this item's own Done-when: "a subsequent GET never includes it").
    private static WebhookEndpointDto ToDto(WebhookEndpoint e) => new(e.Id.Value, e.Url.ToString(), e.Active, e.CreatedAt);
}
