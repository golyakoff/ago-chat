using System.Text.Json;
using Ago.Chat.Contracts;
using Ago.Platform.Abstractions;
using Ago.Platform.Kernel;

namespace Ago.Chat.Application.Mapping;

/// <summary>
/// `22-07`: builds the <see cref="ModuleQuantityGranted"/> envelope for its one publisher today,
/// <c>Ago.Chat.Infrastructure.Postgres.ModuleQuantityGrantStore</c> - a single mapper rather than a
/// call site building the envelope inline, the same reason <see cref="RoleAssignmentsChangedMapper"/>
/// exists: the envelope's shape (<see cref="EventEnvelope.PartitionKey"/>'s value, the topic name, the
/// version) is a decision made once.
///
/// <para><b>Keyed by <see cref="ModuleQuantityGranted.SiteId"/>, not by module key.</b> Ordering only
/// matters between successive grants for the same tenant's own module - keying by module key instead
/// would queue every tenant's calendar grant behind every other tenant's, for a guarantee nobody
/// needs (the identical reasoning <see cref="RoleAssignmentsChangedMapper"/>'s own remarks give for
/// keying by subject rather than by site).</para>
/// </summary>
public static class ModuleQuantityGrantedMapper
{
    public static EventEnvelope ToEnvelope(
        Guid siteId, string moduleKey, int quantity, DateTimeOffset occurredAt, IIdGenerator idGenerator)
    {
        var contract = new ModuleQuantityGranted(
            SiteId: siteId,
            ModuleKey: moduleKey,
            Quantity: quantity,
            CorrelationId: idGenerator.NewId(occurredAt),
            OccurredAt: occurredAt);

        return new EventEnvelope(
            MessageId: idGenerator.NewId(occurredAt),
            Type: nameof(ModuleQuantityGranted),
            Version: 1,
            PartitionKey: contract.SiteId.ToString(),
            OccurredAt: contract.OccurredAt,
            CorrelationId: contract.CorrelationId,
            Payload: JsonSerializer.Serialize(contract));
    }
}
