using System.Text.Json;
using Ago.Chat.Contracts;
using Ago.Chat.Domain;
using Ago.Platform.Abstractions;
using Ago.Platform.Kernel;

namespace Ago.Chat.Application.Mapping;

/// <summary>
/// `25-119`: builds the outbox row for a message's own delivery ack. Unlike most mappers in this folder,
/// there is no <see cref="Domain.IDomainEvent"/> to map from - <see cref="Message.MarkDelivered"/>
/// mutates a value the aggregate exposes, not one it raises an event for, the same "no conversation
/// state changes, so there is no domain event" shape <c>AttachmentUploadGrantChangedMapper</c>'s own
/// remarks state for its sibling - <c>AcknowledgeMessageDeliveredHandler</c> calls this directly with
/// the facts it already has in hand.
///
/// <para>Keyed by <see cref="MessageDelivered.ConversationId"/>, matching every other per-conversation
/// mapper in this folder - the only ordering that matters is between successive delivery acks on the
/// same conversation.</para>
/// </summary>
public static class MessageDeliveredMapper
{
    public static EventEnvelope ToEnvelope(
        ConversationId conversationId, MessageId messageId, OperatorId operatorId, DateTimeOffset deliveredAt,
        IIdGenerator idGenerator)
    {
        var contract = new MessageDelivered(
            ConversationId: conversationId.Value,
            MessageId: messageId.Value,
            OperatorId: operatorId.Value,
            DeliveredAt: deliveredAt);

        return new EventEnvelope(
            MessageId: idGenerator.NewId(deliveredAt),
            Type: nameof(MessageDelivered),
            Version: 1,
            PartitionKey: contract.ConversationId.ToString(),
            OccurredAt: contract.DeliveredAt,
            CorrelationId: idGenerator.NewId(deliveredAt),
            Payload: JsonSerializer.Serialize(contract));
    }
}
