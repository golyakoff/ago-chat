using System.Text.Json;
using Ago.Chat.Contracts;
using Ago.Chat.Domain;
using Ago.Platform.Abstractions;
using Ago.Platform.Kernel;

namespace Ago.Chat.Application.Mapping;

/// <summary>
/// `23-48`: the CORS-layer half of <see cref="SiteAllowedOriginsUpdated"/>'s two mappings - produces
/// <see cref="SiteAllowedOriginsChanged"/>, read by the new <c>SiteAllowedOriginsCacheInvalidationConsumer</c>
/// to evict <c>CorsOriginCacheKeys.ForOrigin</c> for every origin this write touched (both lists - see
/// that contract's own remarks for why both, not a delta). See
/// <see cref="SiteAllowedOriginsUpdatedMapper"/> for this same domain event's other mapping, the
/// chat-internal cache-invalidation one every other <see cref="Site"/> settings write already uses.
///
/// <para>A separate <see cref="EventEnvelope.MessageId"/> from the sibling mapping's envelope,
/// matching <see cref="ContactVisibilityChangedMapper"/>'s own precedent for a domain event mapped
/// twice: two independently-retryable outbox rows, not one shared id two different payloads would
/// have to agree on.</para>
/// </summary>
public static class SiteAllowedOriginsChangedMapper
{
    public static EventEnvelope ToEnvelope(SiteAllowedOriginsUpdated domainEvent, IIdGenerator idGenerator)
    {
        var messageId = idGenerator.NewId(domainEvent.OccurredAt);
        var contract = new SiteAllowedOriginsChanged(
            MessageId: messageId,
            OccurredAt: domainEvent.OccurredAt,
            SiteId: domainEvent.SiteId.Value,
            CorrelationId: idGenerator.NewId(domainEvent.OccurredAt),
            PublicKey: domainEvent.PublicKey,
            PreviousOrigins: domainEvent.PreviousOrigins,
            AllowedOrigins: domainEvent.AllowedOrigins);

        return new EventEnvelope(
            MessageId: messageId,
            Type: nameof(SiteAllowedOriginsChanged),
            Version: 1,
            PartitionKey: contract.SiteId.ToString(),
            OccurredAt: contract.OccurredAt,
            CorrelationId: contract.CorrelationId,
            Payload: JsonSerializer.Serialize(contract));
    }
}
