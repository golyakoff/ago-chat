using System.Text.Json;
using Ago.Chat.Contracts;
using Ago.Chat.Domain;
using Ago.Platform.Abstractions;
using Ago.Platform.Kernel;

namespace Ago.Chat.Application.Mapping;

/// <summary>
/// Domain event -> integration event -> <see cref="EventEnvelope"/>, in one place - the only place
/// <c>Ago.Chat.Domain.MessageAdded</c> and <c>Ago.Chat.Contracts.MessageAccepted</c> meet
/// (`clean-architecture.md`: mapping happens in Application when writing to the outbox, never a
/// shared type between Domain and Contracts).
/// </summary>
public static class MessageAcceptedMapper
{
    public static EventEnvelope ToEnvelope(MessageAdded domainEvent, IIdGenerator idGenerator)
    {
        var contract = new MessageAccepted(
            MessageId: domainEvent.MessageId.Value,
            OccurredAt: domainEvent.OccurredAt,
            SiteId: domainEvent.SiteId.Value,
            // No request-tracing correlation id exists to thread through yet (Stage 7) - a fresh id
            // per event is the honest choice today, not a borrowed one that would imply a link that
            // isn't actually there.
            CorrelationId: idGenerator.NewId(domainEvent.OccurredAt),
            ConversationId: domainEvent.ConversationId.Value,
            AuthorKind: domainEvent.AuthorKind.ToString(),
            Sequence: domainEvent.Sequence);

        return new EventEnvelope(
            MessageId: contract.MessageId,
            Type: nameof(MessageAccepted),
            Version: 1,
            PartitionKey: contract.ConversationId.ToString(),
            OccurredAt: contract.OccurredAt,
            CorrelationId: contract.CorrelationId,
            Payload: JsonSerializer.Serialize(contract));
    }
}
