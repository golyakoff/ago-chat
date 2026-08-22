using System.Text.Json;
using Ago.Chat.Contracts;
using Ago.Chat.Domain;
using Ago.Platform.Abstractions;
using Ago.Platform.Kernel;

namespace Ago.Chat.Application.Mapping;

/// <summary>
/// Domain event -> integration event -> <see cref="EventEnvelope"/>, in one place - the only place
/// <c>Ago.Chat.Domain.ConversationAssigned</c> and <c>Ago.Chat.Contracts.ConversationAssignedToOperator</c>
/// meet (`clean-architecture.md`: mapping happens in Application when writing to the outbox, never a
/// shared type between Domain and Contracts). <paramref name="siteId"/>/<paramref name="visitorId"/>
/// are not on the domain event itself (<c>AssignTo</c> only ever needed <c>OperatorId</c>) - the
/// caller already has them from the <see cref="Conversation"/> aggregate it just mutated, so passing
/// them here costs nothing and avoids a second load this mapper has no business doing itself.
/// </summary>
public static class ConversationAssignedToOperatorMapper
{
    public static EventEnvelope ToEnvelope(
        ConversationAssigned domainEvent, SiteId siteId, VisitorId visitorId, IIdGenerator idGenerator)
    {
        // Unlike MessageAdded, this domain event has no entity id of its own to reuse as the
        // envelope's MessageId - two separate generated ids, not one reused for both fields:
        // MessageId identifies this specific outboxed event (what a future idempotency check would
        // key on), CorrelationId is for tracing a causal chain (Stage 7) and could legitimately
        // repeat across related events one day, which MessageId must never do.
        var eventId = idGenerator.NewId(domainEvent.OccurredAt);
        var contract = new ConversationAssignedToOperator(
            ConversationId: domainEvent.ConversationId.Value,
            SiteId: siteId.Value,
            VisitorId: visitorId.Value,
            OperatorId: domainEvent.OperatorId.Value,
            CorrelationId: idGenerator.NewId(domainEvent.OccurredAt),
            OccurredAt: domainEvent.OccurredAt);

        return new EventEnvelope(
            MessageId: eventId,
            Type: nameof(ConversationAssignedToOperator),
            Version: 1,
            PartitionKey: contract.ConversationId.ToString(),
            OccurredAt: contract.OccurredAt,
            CorrelationId: contract.CorrelationId,
            Payload: JsonSerializer.Serialize(contract));
    }
}
