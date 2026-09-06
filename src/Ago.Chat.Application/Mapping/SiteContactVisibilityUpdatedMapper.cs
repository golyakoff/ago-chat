using System.Text.Json;
using Ago.Chat.Contracts;
using Ago.Chat.Domain;
using Ago.Platform.Abstractions;
using Ago.Platform.Kernel;

namespace Ago.Chat.Application.Mapping;

/// <summary>
/// `23-11`: the chat-internal half of <see cref="SiteContactVisibilityUpdated"/>'s two mappings -
/// maps to the same <see cref="SiteSettingsChanged"/> contract every other <see cref="Site"/> settings
/// write already converges on (<see cref="SiteWidgetConfigUpdatedMapper"/>'s own precedent), so the
/// existing <c>SiteCacheInvalidationConsumer</c> evicts this site's cached <c>SiteConfigDto</c> with no
/// new consumer code at all. See <see cref="ContactVisibilityChangedMapper"/> for this same domain
/// event's other mapping, the one that crosses the product boundary.
/// </summary>
public static class SiteContactVisibilityUpdatedMapper
{
    public static EventEnvelope ToEnvelope(SiteContactVisibilityUpdated domainEvent, IIdGenerator idGenerator)
    {
        var messageId = idGenerator.NewId(domainEvent.OccurredAt);
        var contract = new SiteSettingsChanged(
            MessageId: messageId,
            OccurredAt: domainEvent.OccurredAt,
            SiteId: domainEvent.SiteId.Value,
            CorrelationId: idGenerator.NewId(domainEvent.OccurredAt),
            PublicKey: domainEvent.PublicKey);

        return new EventEnvelope(
            MessageId: messageId,
            Type: nameof(SiteSettingsChanged),
            Version: 1,
            PartitionKey: contract.SiteId.ToString(),
            OccurredAt: contract.OccurredAt,
            CorrelationId: contract.CorrelationId,
            Payload: JsonSerializer.Serialize(contract));
    }
}
