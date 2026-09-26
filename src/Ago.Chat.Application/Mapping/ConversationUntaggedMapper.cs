using System.Text.Json;
using Ago.Chat.Contracts;
using Ago.Platform.Abstractions;
using Ago.Platform.Kernel;

namespace Ago.Chat.Application.Mapping;

/// <summary>
/// `adr/0186` S1: the mirror of <see cref="ConversationTaggedMapper"/>, for
/// <c>Ago.Chat.Infrastructure.Postgres.TagRepository.RemoveFromConversationAsync</c>'s own
/// <see cref="ConversationUntagged"/> envelope.
/// </summary>
public static class ConversationUntaggedMapper
{
    public static EventEnvelope ToEnvelope(
        Guid conversationId, Guid siteId, Guid tagId, DateTimeOffset occurredAt, IIdGenerator idGenerator)
    {
        var contract = new ConversationUntagged(
            ConversationId: conversationId,
            SiteId: siteId,
            TagId: tagId,
            OccurredAt: occurredAt,
            CorrelationId: idGenerator.NewId(occurredAt));

        return new EventEnvelope(
            MessageId: idGenerator.NewId(occurredAt),
            Type: nameof(ConversationUntagged),
            Version: 1,
            PartitionKey: contract.ConversationId.ToString(),
            OccurredAt: contract.OccurredAt,
            CorrelationId: contract.CorrelationId,
            Payload: JsonSerializer.Serialize(contract));
    }
}
