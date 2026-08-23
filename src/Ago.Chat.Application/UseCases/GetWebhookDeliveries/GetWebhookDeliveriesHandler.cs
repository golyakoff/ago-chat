using Ago.Chat.Application.Abstractions;
using Ago.Chat.Contracts;
using Ago.Chat.Domain;
using Ago.Platform.Kernel;

namespace Ago.Chat.Application.UseCases.GetWebhookDeliveries;

/// <summary>
/// `6-03`'s own scope: gated by `webhook:manage` too, the same permission as registration/revocation -
/// "reading delivery history is not more sensitive than managing the endpoint that produces it." Never
/// filters on `WebhookEndpoint.Active` - history for a revoked endpoint stays queryable
/// (`RevokeWebhookEndpointHandler`'s own remarks).
/// </summary>
public sealed class GetWebhookDeliveriesHandler(
    IWebhookEndpointRepository endpoints, IWebhookDeliveryReadStore deliveries, IPermissionChecker permissions)
{
    public async Task<Result<WebhookDeliveriesResponse>> HandleAsync(
        GetWebhookDeliveries query, CancellationToken cancellationToken)
    {
        var allowed = await permissions.HasPermissionAsync(
            query.RequestedBy, query.SiteId, Permission.WebhookManage, cancellationToken);
        if (!allowed)
        {
            return ConversationErrors.Forbidden("Operator does not have permission to manage webhooks for this site.");
        }

        var endpoint = await endpoints.GetByIdAsync(query.WebhookEndpointId, cancellationToken);
        if (endpoint is null || endpoint.SiteId != query.SiteId)
        {
            return ConversationErrors.WebhookEndpointNotFound(query.WebhookEndpointId.Value);
        }

        var page = await deliveries.GetForEndpointAsync(query.WebhookEndpointId, query.BeforeId, query.PageSize, cancellationToken);

        return new WebhookDeliveriesResponse(page.Deliveries.Select(ToDto).ToList(), page.NextBeforeId);
    }

    private static WebhookDeliveryDto ToDto(WebhookDeliverySummaryItem item) => new(
        item.Id.Value, item.EventType, item.Attempt, item.Status.ToString(), item.ResponseStatus, item.ResponseSnippet,
        item.CreatedAt, item.DeliveredAt);
}
