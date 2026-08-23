namespace Ago.Chat.Contracts;

/// <summary>
/// `6-03`: `GET /api/v1/sites/{siteId}/webhooks`'s response body - no secret field, ever. The secret
/// is returned exactly once, by `POST`'s own response (`RegisterWebhookEndpointHandler`'s
/// `RegisteredWebhookEndpoint`, deliberately not this type), never again by any subsequent read.
/// </summary>
public sealed record WebhookEndpointDto(Guid Id, string Url, bool Active, DateTimeOffset CreatedAt);

public sealed record WebhookEndpointsResponse(IReadOnlyList<WebhookEndpointDto> Endpoints);
