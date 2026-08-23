using Ago.Chat.Domain;

namespace Ago.Chat.Application.UseCases.RevokeWebhookEndpoint;

public sealed record RevokeWebhookEndpoint(WebhookEndpointId WebhookEndpointId, OperatorId RequestedBy, SiteId SiteId);
