using System.Text.Json;
using Ago.Chat.Contracts;
using Ago.Chat.Domain;
using Ago.Platform.Abstractions;
using Ago.Platform.Kernel;

namespace Ago.Chat.Application.Mapping;

/// <summary>
/// `23-11`: the cross-product half of <see cref="SiteContactVisibilityUpdated"/>'s two mappings - the
/// one contract `23-12`'s calendar-side consumer reads, the same role
/// <see cref="RoleAssignmentsChangedMapper"/> plays for the role catalogue. See
/// <see cref="SiteContactVisibilityUpdatedMapper"/> for this same domain event's other mapping, the
/// chat-internal cache-invalidation one.
///
/// <para><b>Keyed by <see cref="ContactVisibilityChanged.SiteId"/>, not by an external subject.</b>
/// Unlike <see cref="RoleAssignmentsChangedMapper"/> (keyed by subject, because the only ordering that
/// matters is between successive facts about the same person), this fact has no subject at all - the
/// only ordering that matters is between successive rung changes on the same site, which
/// <see cref="EventEnvelope.PartitionKey"/> = site id already guarantees.</para>
/// </summary>
public static class ContactVisibilityChangedMapper
{
    public static EventEnvelope ToEnvelope(SiteContactVisibilityUpdated domainEvent, IIdGenerator idGenerator)
    {
        var contract = new ContactVisibilityChanged(
            SiteId: domainEvent.SiteId.Value,
            Rung: domainEvent.Rung.ToString(),
            CorrelationId: idGenerator.NewId(domainEvent.OccurredAt),
            OccurredAt: domainEvent.OccurredAt);

        return new EventEnvelope(
            MessageId: idGenerator.NewId(domainEvent.OccurredAt),
            Type: nameof(ContactVisibilityChanged),
            Version: 1,
            PartitionKey: contract.SiteId.ToString(),
            OccurredAt: contract.OccurredAt,
            CorrelationId: contract.CorrelationId,
            Payload: JsonSerializer.Serialize(contract));
    }
}
