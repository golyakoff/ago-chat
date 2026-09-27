using System.Text.Json;
using Ago.Chat.Contracts;
using Ago.Platform.Abstractions;
using Ago.Platform.Kernel;

namespace Ago.Chat.Application.Mapping;

/// <summary>
/// `adr/0186` S1: the mirror of <see cref="ConversationTaggedMapper"/>, for
/// <c>Ago.Chat.Infrastructure.Postgres.TagRepository.RemoveFromConversationAsync</c>'s own
/// <see cref="ConversationUntagged"/> envelope.
///
/// <para><b>`26-215`: <paramref name="tenantZone"/> joins this mapper</b>, on the identical terms
/// <see cref="ConversationTaggedMapper"/>'s own remarks give for the identical parameter.</para>
/// </summary>
public static class ConversationUntaggedMapper
{
    public static EventEnvelope ToEnvelope(
        Guid conversationId, Guid siteId, Guid tagId, DateTimeOffset occurredAt, string tenantZone,
        IIdGenerator idGenerator)
    {
        var contract = new ConversationUntagged(
            ConversationId: conversationId,
            SiteId: siteId,
            TagId: tagId,
            OccurredAt: occurredAt,
            CorrelationId: idGenerator.NewId(occurredAt),
            TenantZone: tenantZone);

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
