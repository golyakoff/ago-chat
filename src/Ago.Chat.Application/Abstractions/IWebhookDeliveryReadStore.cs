using Ago.Chat.Domain;

namespace Ago.Chat.Application.Abstractions;

/// <summary>
/// The read-side port for delivery history: hand-written SQL over the write model, never through the
/// aggregate (adr/0004) - the same split <see cref="IConversationReadStore"/> already draws between
/// itself and <see cref="IConversationRepository"/>. Keyset-shaped from the start, like every other
/// read store in this codebase; unlike <see cref="IConversationReadStore.GetHistoryAsync"/> there is
/// no state filter - `GetWebhookDeliveriesHandler`'s own scope note: history stays queryable for a
/// revoked endpoint too, so this never filters on <see cref="WebhookEndpoint.Active"/>.
/// </summary>
public interface IWebhookDeliveryReadStore
{
    Task<WebhookDeliveryPage> GetForEndpointAsync(
        WebhookEndpointId endpointId, Guid? beforeId, int pageSize, CancellationToken cancellationToken);
}
