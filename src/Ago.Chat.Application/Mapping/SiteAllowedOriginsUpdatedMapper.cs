using System.Text.Json;
using Ago.Chat.Contracts;
using Ago.Chat.Domain;
using Ago.Platform.Abstractions;
using Ago.Platform.Kernel;

namespace Ago.Chat.Application.Mapping;

/// <summary>
/// `23-48`: the chat-internal half of <see cref="SiteAllowedOriginsUpdated"/>'s two mappings - maps to
/// the same <see cref="SiteSettingsChanged"/> contract every other <see cref="Site"/> settings write
/// already converges on (<see cref="SiteWidgetConfigUpdatedMapper"/>'s own precedent), so the existing
/// <c>SiteCacheInvalidationConsumer</c> evicts this site's cached <c>SiteConfigDto</c>
/// (<c>SiteCacheKeys.ForPublicKey</c>/<c>ForSiteId</c>) with no new consumer code at all - the widget
/// handshake and the hub's layer-2 origin check both read <c>AllowedOrigins</c> off that same cached
/// row. See <see cref="SiteAllowedOriginsChangedMapper"/> for this same domain event's other mapping,
/// the one that carries what the new CORS-layer consumer needs.
/// </summary>
public static class SiteAllowedOriginsUpdatedMapper
{
    public static EventEnvelope ToEnvelope(SiteAllowedOriginsUpdated domainEvent, IIdGenerator idGenerator)
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
