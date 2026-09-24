using System.Text.Json;
using Ago.Chat.Contracts;
using Ago.Chat.Domain;
using Ago.Platform.Abstractions;
using Ago.Platform.Kernel;

namespace Ago.Chat.Application.Mapping;

/// <summary>
/// Domain event -> integration event -> <see cref="EventEnvelope"/>, in one place - the only place
/// <c>Ago.Chat.Domain.ConversationEnteredQueue</c> and <c>Ago.Chat.Contracts.ConversationWaitingForOperator</c>
/// meet (`clean-architecture.md`: mapping happens in Application when writing to the outbox, never a
/// shared type between Domain and Contracts). Unlike <see cref="ConversationReleasedToQueueMapper"/>,
/// no extra <c>siteId</c>/<c>visitorId</c> parameters are needed - <see cref="ConversationEnteredQueue"/>
/// already carries both, since it fires from <c>Conversation.AddVisitorMessage</c>, which always has the
/// full aggregate in hand.
/// </summary>
public static class ConversationWaitingForOperatorMapper
{
    public static EventEnvelope ToEnvelope(ConversationEnteredQueue domainEvent, IIdGenerator idGenerator)
    {
        var eventId = idGenerator.NewId(domainEvent.OccurredAt);
        var contract = new ConversationWaitingForOperator(
            ConversationId: domainEvent.ConversationId.Value,
            SiteId: domainEvent.SiteId.Value,
            VisitorId: domainEvent.VisitorId.Value,
            // No request-tracing correlation id exists to thread through yet (Stage 7) - a fresh id
            // per event is the honest choice today, the same reasoning MessageAcceptedMapper's own
            // remarks give for the identical gap.
            CorrelationId: idGenerator.NewId(domainEvent.OccurredAt),
            OccurredAt: domainEvent.OccurredAt);

        return new EventEnvelope(
            MessageId: eventId,
            Type: nameof(ConversationWaitingForOperator),
            Version: 1,
            PartitionKey: contract.ConversationId.ToString(),
            OccurredAt: contract.OccurredAt,
            CorrelationId: contract.CorrelationId,
            Payload: JsonSerializer.Serialize(contract));
    }
}
