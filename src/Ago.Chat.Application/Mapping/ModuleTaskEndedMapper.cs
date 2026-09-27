using System.Text.Json;
using Ago.Chat.Contracts;
using Ago.Chat.Domain;
using Ago.Platform.Abstractions;
using Ago.Platform.Kernel;

namespace Ago.Chat.Application.Mapping;

/// <summary>
/// `26-237`/`adr/0186` (module-flow funnel rollup): domain event -> integration event ->
/// <see cref="EventEnvelope"/>, the sibling of <see cref="ModuleTaskOpenedMapper"/>.
///
/// <para><b><see cref="EventEnvelope.OccurredAt"/> is the task's open instant, not its close.</b> The
/// funnel report windows by <c>module_tasks.opened_at</c>, and the funnel rollup pairs a close with its
/// start inside the open day's slice, so the raw analytics layer must bucket this close event by the day
/// the task <em>opened</em> - hence the envelope instant is <c>domainEvent.OpenedAt</c>, even though the
/// event is emitted at close. This mirrors <see cref="ConversationAssignmentClosedMapper"/>'s own
/// "OccurredAt is the interval's start" choice exactly. The real close instant travels in the body
/// (<see cref="ModuleTaskEnded.ClosedAt"/>) for any future report that wants it.</para>
/// </summary>
public static class ModuleTaskEndedMapper
{
    public static EventEnvelope ToEnvelope(ModuleTaskClosed domainEvent, string tenantZone, IIdGenerator idGenerator)
    {
        var contract = new ModuleTaskEnded(
            ConversationId: domainEvent.ConversationId.Value,
            SiteId: domainEvent.SiteId.Value,
            ModuleTaskId: domainEvent.ModuleTaskId.Value,
            ModuleKey: domainEvent.ModuleKey.Value,
            OpenedAt: domainEvent.OpenedAt,
            ClosedAt: domainEvent.OccurredAt,
            OccurredAt: domainEvent.OpenedAt,
            CorrelationId: idGenerator.NewId(domainEvent.OccurredAt),
            TenantZone: tenantZone);

        return new EventEnvelope(
            // A fresh id per publish, not the task id: the ModuleTaskOpened envelope already claims the task
            // id as its MessageId, and the two must not collide in the outbox/raw dedup. The raw layer dedups
            // on this id and a close is a once-per-task transition, so redelivery still collapses to one row.
            MessageId: idGenerator.NewId(domainEvent.OccurredAt),
            Type: nameof(ModuleTaskEnded),
            Version: 1,
            PartitionKey: contract.ConversationId.ToString(),
            // The open instant, so the raw layer buckets this close into the same day-slice as its start
            // (see the contract's remarks).
            OccurredAt: contract.OccurredAt,
            CorrelationId: contract.CorrelationId,
            Payload: JsonSerializer.Serialize(contract));
    }
}
