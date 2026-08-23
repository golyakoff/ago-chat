using Ago.Chat.Application.Abstractions;
using Ago.Chat.Domain;

namespace Ago.Chat.Application.Tests.Fakes;

public sealed class FakeWebhookDeliveryRepository : IWebhookDeliveryRepository
{
    public List<WebhookDelivery> Saved { get; } = [];

    public Task SaveAsync(WebhookDelivery delivery, CancellationToken cancellationToken)
    {
        Saved.Add(delivery);
        return Task.CompletedTask;
    }
}
