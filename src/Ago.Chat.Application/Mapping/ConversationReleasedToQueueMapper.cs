using System.Text.Json;
using Ago.Chat.Contracts;
using Ago.Chat.Domain;
using Ago.Platform.Abstractions;
using Ago.Platform.Kernel;

namespace Ago.Chat.Application.Mapping;

/// <summary>
/// Domain event -> integration event -> <see cref="EventEnvelope"/>, in one place - the only place
/// <c>Ago.Chat.Domain.ConversationReleased</c> and <c>Ago.Chat.Contracts.ConversationReleasedToQueue</c>
/// meet. <paramref name="siteId"/>/<paramref name="visitorId"/> are not on the domain event itself
/// (<c>ReleaseToQueue</c> only ever needed the previous <c>OperatorId</c>) - the caller already has
/// them from the <see cref="Conversation"/> aggregate it just mutated.
/// </summary>
public static class ConversationReleasedToQueueMapper
{
    public static EventEnvelope ToEnvelope(
        ConversationReleased domainEvent, SiteId siteId, VisitorId visitorId, IIdGenerator idGenerator)
    {
        var eventId = idGenerator.NewId(domainEvent.OccurredAt);
        var contract = new ConversationReleasedToQueue(
            ConversationId: domainEvent.ConversationId.Value,
            SiteId: siteId.Value,
            VisitorId: visitorId.Value,
            CorrelationId: idGenerator.NewId(domainEvent.OccurredAt),
            OccurredAt: domainEvent.OccurredAt);

        return new EventEnvelope(
            MessageId: eventId,
            Type: nameof(ConversationReleasedToQueue),
            Version: 1,
            PartitionKey: contract.ConversationId.ToString(),
            OccurredAt: contract.OccurredAt,
            CorrelationId: contract.CorrelationId,
            Payload: JsonSerializer.Serialize(contract));
    }
}
