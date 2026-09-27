using System.Text.Json;
using Ago.Chat.Contracts;
using Ago.Platform.Abstractions;
using Ago.Platform.Kernel;

namespace Ago.Chat.Application.Mapping;

/// <summary>
/// `adr/0186` S1: builds the <see cref="ConversationTagged"/> envelope for its one publisher,
/// <c>Ago.Chat.Infrastructure.Postgres.TagRepository.AddToConversationAsync</c> - raw values in, not a
/// domain event, the same shape <see cref="ModuleQuantityGrantedMapper"/> already uses for a write with
/// no aggregate of its own to raise one (<see cref="ConversationTagged"/>'s own remarks on why tags have
/// none).
///
/// <para><b>`26-215`: <paramref name="tenantZone"/> joins this mapper</b>, resolved by
/// <c>TagRepository</c> itself (the one caller) exactly as <see cref="Contracts.ConversationTagged"/>'s
/// own remarks describe.</para>
/// </summary>
public static class ConversationTaggedMapper
{
    public static EventEnvelope ToEnvelope(
        Guid conversationId, Guid siteId, Guid tagId, DateTimeOffset occurredAt, string tenantZone,
        IIdGenerator idGenerator)
    {
        var contract = new ConversationTagged(
            ConversationId: conversationId,
            SiteId: siteId,
            TagId: tagId,
            OccurredAt: occurredAt,
            CorrelationId: idGenerator.NewId(occurredAt),
            TenantZone: tenantZone);

        return new EventEnvelope(
            MessageId: idGenerator.NewId(occurredAt),
            Type: nameof(ConversationTagged),
            Version: 1,
            PartitionKey: contract.ConversationId.ToString(),
            OccurredAt: contract.OccurredAt,
            CorrelationId: contract.CorrelationId,
            Payload: JsonSerializer.Serialize(contract));
    }
}
