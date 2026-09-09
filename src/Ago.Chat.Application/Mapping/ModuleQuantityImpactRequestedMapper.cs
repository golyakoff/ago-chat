using System.Text.Json;
using Ago.Chat.Contracts;
using Ago.Platform.Abstractions;
using Ago.Platform.Kernel;

namespace Ago.Chat.Application.Mapping;

/// <summary>
/// `23-88`: builds the <see cref="ModuleQuantityImpactRequested"/> envelope, the identical role
/// <see cref="ModuleQuantityGrantedMapper"/> plays for its own sibling event - one call site
/// (<c>Ago.Chat.Infrastructure.Postgres.ModuleQuantityImpactPreviewStore</c>) but a named mapper
/// anyway, so the envelope's shape is a decision made once rather than inlined where it happens to be
/// needed first.
///
/// <para>Keyed by <see cref="ModuleQuantityImpactRequested.SiteId"/>, the identical reasoning
/// <see cref="ModuleQuantityGrantedMapper"/>'s own remarks give: ordering only matters between
/// successive questions about the same tenant's own module.</para>
/// </summary>
public static class ModuleQuantityImpactRequestedMapper
{
    public static EventEnvelope ToEnvelope(
        Guid siteId, string moduleKey, int requestedQuantity, DateTimeOffset occurredAt, IIdGenerator idGenerator)
    {
        var contract = new ModuleQuantityImpactRequested(
            SiteId: siteId,
            ModuleKey: moduleKey,
            RequestedQuantity: requestedQuantity,
            CorrelationId: idGenerator.NewId(occurredAt),
            OccurredAt: occurredAt);

        return new EventEnvelope(
            MessageId: idGenerator.NewId(occurredAt),
            Type: nameof(ModuleQuantityImpactRequested),
            Version: 1,
            PartitionKey: contract.SiteId.ToString(),
            OccurredAt: contract.OccurredAt,
            CorrelationId: contract.CorrelationId,
            Payload: JsonSerializer.Serialize(contract));
    }
}
