using Ago.Chat.Application.Abstractions;
using Ago.Chat.Domain;

namespace Ago.Chat.Application.Tests.Fakes;

/// <summary>In-memory keyset pagination over seeded items, newest-first - mirrors
/// <see cref="WebhookDeliveryReadStore"/>'s own `WHERE endpoint_id = @x AND id &lt; @cursor ORDER BY
/// id DESC LIMIT` shape closely enough that a handler test exercising pagination behaves the same
/// against either. Guids do not have a natural creation-time ordering the way a real uuid v7 does
/// (`IIdGenerator`), so tests seed items in the order they want returned and this store preserves
/// insertion order rather than sorting by the raw <see cref="Guid"/> bytes.</summary>
public sealed class FakeWebhookDeliveryReadStore : IWebhookDeliveryReadStore
{
    private readonly List<(WebhookEndpointId EndpointId, WebhookDeliverySummaryItem Item)> _items = [];

    /// <summary>Seed newest-first - the same order <see cref="GetForEndpointAsync"/> returns.</summary>
    public void Seed(WebhookEndpointId endpointId, WebhookDeliverySummaryItem item) => _items.Add((endpointId, item));

    public Task<WebhookDeliveryPage> GetForEndpointAsync(
        WebhookEndpointId endpointId, Guid? beforeId, int pageSize, CancellationToken cancellationToken)
    {
        var forEndpoint = _items.Where(x => x.EndpointId == endpointId).Select(x => x.Item);

        if (beforeId is { } cursor)
        {
            var cursorIndexReached = false;
            forEndpoint = forEndpoint.SkipWhile(i =>
            {
                if (cursorIndexReached)
                {
                    return false;
                }

                cursorIndexReached = i.Id.Value == cursor;
                return true;
            });
        }

        var page = forEndpoint.Take(pageSize).ToList();
        var nextCursor = page.Count == pageSize ? page[^1].Id.Value : (Guid?)null;
        return Task.FromResult(new WebhookDeliveryPage(page, nextCursor));
    }
}
