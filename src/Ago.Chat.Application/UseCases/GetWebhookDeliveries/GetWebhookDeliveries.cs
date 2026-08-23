using Ago.Chat.Domain;

namespace Ago.Chat.Application.UseCases.GetWebhookDeliveries;

/// <summary><paramref name="BeforeId"/> <see langword="null"/> means "most recent page" - the same
/// convention `GetAllConversationsForSite`'s own remarks establish.</summary>
public sealed record GetWebhookDeliveries(
    WebhookEndpointId WebhookEndpointId, OperatorId RequestedBy, SiteId SiteId, Guid? BeforeId, int PageSize);
