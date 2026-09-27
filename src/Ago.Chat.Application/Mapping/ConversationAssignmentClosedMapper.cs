using System.Text.Json;
using Ago.Chat.Contracts;
using Ago.Platform.Abstractions;
using Ago.Platform.Kernel;

namespace Ago.Chat.Application.Mapping;

/// <summary>
/// `26-237`/`adr/0186` (operator-load rollup, decision B): builds the <see cref="ConversationAssignmentClosed"/>
/// analytics envelope from the values the closing adapter resolved at close. Its one publisher is
/// <c>Ago.Chat.Infrastructure.Postgres.ConversationAssignmentLog</c> - the single adapter that closes every
/// interval - which stages this envelope to the outbox in the same unit of work as the close (rule 4), the
/// same "the adapter that owns the write owns its event" shape <c>ConversationTaggedMapper</c>/
/// <c>TagRepository</c> establish for a write with no aggregate to route a domain event through.
///
/// <para><b><see cref="EventEnvelope.OccurredAt"/> is the interval's start, not its close.</b> The
/// operator-load report windows by <c>started_at</c>, so the raw analytics layer must bucket this event by
/// the day the holding period began - hence <paramref name="startedAt"/> is the envelope instant, even
/// though the event is emitted at close. See <see cref="ConversationAssignmentClosed"/>'s own remarks.</para>
///
/// <para><b><see cref="EventEnvelope.MessageId"/> is a fresh id per publish</b>, not the conversation id:
/// an operator can hold, be transferred away, and hold the same conversation again, so the conversation id
/// is not a safe redelivery-idempotency key - each interval close is its own outbox row. This mirrors
/// <c>ConversationOutcomeRecordedMapper</c>'s reasoning for the same choice.</para>
/// </summary>
public static class ConversationAssignmentClosedMapper
{
    public static EventEnvelope ToEnvelope(
        Guid conversationId,
        Guid siteId,
        Guid operatorId,
        DateTimeOffset startedAt,
        DateTimeOffset endedAt,
        int concurrentLoad,
        int capacity,
        DateTimeOffset? firstReplyAt,
        string tenantZone,
        IIdGenerator idGenerator)
    {
        var contract = new ConversationAssignmentClosed(
            ConversationId: conversationId,
            SiteId: siteId,
            OperatorId: operatorId,
            StartedAt: startedAt,
            EndedAt: endedAt,
            ConcurrentLoad: concurrentLoad,
            Capacity: capacity,
            FirstReplyAt: firstReplyAt,
            OccurredAt: startedAt,
            CorrelationId: idGenerator.NewId(endedAt),
            TenantZone: tenantZone);

        return new EventEnvelope(
            MessageId: idGenerator.NewId(endedAt),
            Type: nameof(ConversationAssignmentClosed),
            Version: 1,
            PartitionKey: contract.ConversationId.ToString(),
            // The interval's start, so the raw layer buckets it into the tenant-local day the holding
            // period began - the operator-load report's own window key (see the contract's remarks).
            OccurredAt: contract.OccurredAt,
            CorrelationId: contract.CorrelationId,
            Payload: JsonSerializer.Serialize(contract));
    }
}
