using Ago.Chat.Application.Abstractions;
using Ago.Chat.Domain;

namespace Ago.Chat.Application.Tests.Fakes;

public sealed class FakeWebhookEndpointRepository : IWebhookEndpointRepository
{
    private readonly Dictionary<WebhookEndpointId, WebhookEndpoint> _byId = [];

    public Task<WebhookEndpoint?> GetByIdAsync(WebhookEndpointId id, CancellationToken cancellationToken) =>
        Task.FromResult(_byId.GetValueOrDefault(id));

    public Task<IReadOnlyList<WebhookEndpoint>> GetAllForSiteAsync(SiteId siteId, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<WebhookEndpoint>>(_byId.Values.Where(e => e.SiteId == siteId).ToList());

    public Task SaveAsync(WebhookEndpoint endpoint, CancellationToken cancellationToken)
    {
        _byId[endpoint.Id] = endpoint;
        return Task.CompletedTask;
    }

    public void Seed(WebhookEndpoint endpoint) => _byId[endpoint.Id] = endpoint;
}
