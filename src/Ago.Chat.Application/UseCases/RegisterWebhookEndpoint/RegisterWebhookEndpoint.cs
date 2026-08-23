using Ago.Chat.Domain;

namespace Ago.Chat.Application.UseCases.RegisterWebhookEndpoint;

public sealed record RegisterWebhookEndpoint(OperatorId RequestedBy, SiteId SiteId, Uri Url);

/// <summary><see cref="Secret"/> is the plaintext value - this response is the one and only place it
/// ever leaves this system in that form (this item's own Done-when: "a subsequent GET never includes
/// it").</summary>
public sealed record RegisteredWebhookEndpoint(Guid WebhookEndpointId, string Secret, Uri Url, DateTimeOffset CreatedAt);
