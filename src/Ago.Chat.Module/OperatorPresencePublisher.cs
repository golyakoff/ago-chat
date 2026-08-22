using System.Text.Json;
using Ago.Chat.Contracts;
using Ago.Chat.Domain;
using Ago.Platform.Abstractions;
using Ago.Platform.Kernel;

namespace Ago.Chat.Module;

/// <summary>
/// `4-04`: publishes `OperatorPresenceLost` directly via `IEventPublisher`, not through the outbox -
/// the same shape as `CacheInvalidationPublisher` (`adr/0020`): a presence observation describes no
/// committed state change of its own, so there is nothing to stage in the same transaction as.
/// </summary>
public sealed class OperatorPresencePublisher(IEventPublisher publisher, IClock clock, IIdGenerator idGenerator)
{
    public Task PublishLostAsync(OperatorId operatorId, SiteId siteId, CancellationToken cancellationToken)
    {
        var now = clock.UtcNow;
        var contract = new OperatorPresenceLost(operatorId.Value, siteId.Value, now, idGenerator.NewId(now));

        var envelope = new EventEnvelope(
            MessageId: idGenerator.NewId(now),
            Type: nameof(OperatorPresenceLost),
            Version: 1,
            PartitionKey: operatorId.Value.ToString(),
            OccurredAt: now,
            CorrelationId: contract.CorrelationId,
            Payload: JsonSerializer.Serialize(contract));

        return publisher.PublishAsync(envelope, cancellationToken);
    }
}
