using Ago.Chat.Application.Abstractions;
using Ago.Chat.Domain;

namespace Ago.Chat.Application.Tests.Fakes;

/// <summary>`6-05`: mimics the real repository's own unique-(endpoint_id, message_id) semantics in
/// memory - <see cref="SaveAsync"/> returns <see langword="false"/> for a duplicate pair instead of
/// throwing, the same "already recorded, no-op" contract
/// `Ago.Chat.Infrastructure.Postgres.WebhookDeliveryRepository` gives via a caught unique-index
/// violation, so a handler test exercising the idempotency path does not need a real Postgres to prove
/// it.</summary>
public sealed class FakeWebhookDeliveryRepository : IWebhookDeliveryRepository
{
    public List<WebhookDelivery> Saved { get; } = [];

    public Task<bool> SaveAsync(WebhookDelivery delivery, CancellationToken cancellationToken)
    {
        if (Saved.Any(d => d.EndpointId == delivery.EndpointId && d.MessageId == delivery.MessageId))
        {
            return Task.FromResult(false);
        }

        Saved.Add(delivery);
        return Task.FromResult(true);
    }

    public Task<bool> ExistsAsync(WebhookEndpointId endpointId, Guid messageId, CancellationToken cancellationToken) =>
        Task.FromResult(Saved.Any(d => d.EndpointId == endpointId && d.MessageId == messageId));
}
