namespace Ago.Chat.Contracts;

/// <summary>`6-03`: `GET /api/v1/sites/{siteId}/webhooks/{webhookId}/deliveries`'s response body -
/// keyset-paginated (api-design.md), the same `IReadOnlyList<T> Items, TCursor? NextCursor` shape
/// `AllConversationsForSiteResponse` already uses for the analogous admin list.</summary>
public sealed record WebhookDeliveryDto(
    Guid Id,
    string EventType,
    int Attempt,
    string Status,
    int? ResponseStatus,
    string? ResponseSnippet,
    DateTimeOffset CreatedAt,
    DateTimeOffset? DeliveredAt);

public sealed record WebhookDeliveriesResponse(IReadOnlyList<WebhookDeliveryDto> Deliveries, Guid? NextBeforeId);
