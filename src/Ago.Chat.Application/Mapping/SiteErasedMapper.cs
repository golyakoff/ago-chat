using System.Text.Json;
using Ago.Chat.Contracts;
using Ago.Platform.Abstractions;
using Ago.Platform.Kernel;

namespace Ago.Chat.Application.Mapping;

/// <summary>
/// `25-82`: builds the <see cref="SiteErased"/> envelope for its one publisher today
/// (<c>Ago.Chat.Infrastructure.Postgres.SiteErasurePublisher</c>) - its own file, the identical
/// "the envelope's shape is a decision made once, not re-derived at each call site" reason every other
/// mapper in this folder exists (<see cref="RoleAssignmentsChangedMapper"/>'s own remarks).
///
/// <para><b>Keyed by <see cref="SiteErased.SiteId"/>, not by anything narrower.</b> This is a fact
/// about a tenant, not about a person - unlike <see cref="RoleAssignmentsChanged"/>, which orders by
/// subject because two operators' own role changes have no ordering relationship worth serialising.
/// A tenant has exactly one erasure, ever, so the partition key only has to be stable, not meaningful
/// for reordering.</para>
/// </summary>
public static class SiteErasedMapper
{
    public static EventEnvelope ToEnvelope(Guid siteId, DateTimeOffset occurredAt, IIdGenerator idGenerator)
    {
        var contract = new SiteErased(siteId, occurredAt, idGenerator.NewId(occurredAt));

        return new EventEnvelope(
            MessageId: idGenerator.NewId(occurredAt),
            Type: nameof(SiteErased),
            Version: 1,
            PartitionKey: siteId.ToString(),
            OccurredAt: contract.OccurredAt,
            CorrelationId: contract.CorrelationId,
            Payload: JsonSerializer.Serialize(contract));
    }
}
