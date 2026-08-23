using Ago.Chat.Domain;

namespace Ago.Chat.Application.Abstractions;

/// <summary>
/// The write-side port for the <see cref="WebhookEndpoint"/> aggregate - its own port, not folded
/// into a hypothetical `ISiteRepository` method, the same "its own aggregate, its own transaction
/// boundary" reasoning <see cref="IAttachmentRepository"/>'s own remarks give for not folding into
/// <see cref="IConversationRepository"/>. <see cref="GetAllForSiteAsync"/> is unpaginated - a site's
/// registered endpoints are a small, operator-managed set (unlike delivery history, which only grows
/// and is keyset-paginated through <see cref="IWebhookDeliveryReadStore"/> instead), the same "bounded
/// list, go through the write-side repository" call `IConversationRepository.GetWaitingForSiteAsync`
/// already makes for an analogous case.
/// </summary>
public interface IWebhookEndpointRepository
{
    Task<WebhookEndpoint?> GetByIdAsync(WebhookEndpointId id, CancellationToken cancellationToken);

    Task<IReadOnlyList<WebhookEndpoint>> GetAllForSiteAsync(SiteId siteId, CancellationToken cancellationToken);

    Task SaveAsync(WebhookEndpoint endpoint, CancellationToken cancellationToken);
}
