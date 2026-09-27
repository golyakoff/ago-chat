using System.Text.Json;
using Ago.Chat.Contracts;
using Ago.Chat.Domain;
using Ago.Platform.Abstractions;
using Ago.Platform.Kernel;

namespace Ago.Chat.Application.Mapping;

/// <summary>
/// Domain event -> integration event -> <see cref="EventEnvelope"/>, `adr/0186` S1's own repeat of the
/// pattern <see cref="ConversationClosedMapper"/> already established (clean-architecture.md: mapping
/// happens in Application when writing to the outbox, never a shared type between Domain and
/// Contracts). The wire type is <see cref="Contracts.ConversationOutcomeRecorded"/>, mapped from the
/// domain event <see cref="ConversationOutcomeSet"/> - named differently from it on purpose, see that
/// record's own remarks.
///
/// <see cref="EventEnvelope.MessageId"/> is a fresh id per publish, not the conversation's own id -
/// unlike <see cref="ConversationClosedMapper"/>'s once-only <c>ConversationClosed</c>,
/// <see cref="Conversation.SetOutcome"/> may run more than once for the same conversation (an operator
/// changing their mind), so the conversation id is not a safe redelivery-idempotency key for this
/// event - each recording is its own outbox row.
///
/// <para><b>`26-215`: <paramref name="tenantZone"/> joins this mapper</b> - <c>ConversationOutcomeSet</c>
/// itself carries no site time zone (Domain is not allowed to know one, rule 1), so
/// <c>SetConversationOutcomeHandler</c> resolves it the same way <see cref="ConversationOpenedMapper"/>'s
/// own caller does and hands it in here.</para>
/// </summary>
public static class ConversationOutcomeRecordedMapper
{
    public static EventEnvelope ToEnvelope(
        ConversationOutcomeSet domainEvent, string tenantZone, IIdGenerator idGenerator)
    {
        var contract = new ConversationOutcomeRecorded(
            ConversationId: domainEvent.ConversationId.Value,
            SiteId: domainEvent.SiteId.Value,
            Outcome: domainEvent.Outcome.ToString(),
            OccurredAt: domainEvent.OccurredAt,
            CorrelationId: idGenerator.NewId(domainEvent.OccurredAt),
            TenantZone: tenantZone);

        return new EventEnvelope(
            MessageId: idGenerator.NewId(domainEvent.OccurredAt),
            Type: nameof(ConversationOutcomeRecorded),
            Version: 1,
            PartitionKey: contract.ConversationId.ToString(),
            OccurredAt: contract.OccurredAt,
            CorrelationId: contract.CorrelationId,
            Payload: JsonSerializer.Serialize(contract));
    }
}
