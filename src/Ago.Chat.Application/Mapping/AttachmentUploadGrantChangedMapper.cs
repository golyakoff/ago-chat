using System.Text.Json;
using Ago.Chat.Contracts;
using Ago.Chat.Domain;
using Ago.Platform.Abstractions;
using Ago.Platform.Kernel;

namespace Ago.Chat.Application.Mapping;

/// <summary>
/// `25-110`: builds the one outbox row `GrantAttachmentUploadHandler`/`RevokeAttachmentUploadHandler`
/// were missing entirely (the backlog item's own root cause). Unlike every other mapper in this folder,
/// there is no domain event to map from - granting/revoking an upload never touches the
/// <see cref="Conversation"/> aggregate at all (<see cref="Application.Abstractions.IConversationAttachmentUploadGrantRepository"/>'s
/// own remarks: the write is a raw-SQL bypass built specifically to avoid the aggregate's load-mutate-save
/// shape), so there is no <c>DomainEvents</c> list to read this from - both handlers call this directly
/// with the facts they already have in hand.
///
/// <para>Keyed by <see cref="AttachmentUploadGrantChanged.ConversationId"/>, matching
/// <see cref="ConversationClosedMapper"/>'s own choice for the identical reason: the only ordering that
/// matters is between successive grant/revoke acts on the same conversation.</para>
/// </summary>
public static class AttachmentUploadGrantChangedMapper
{
    public static EventEnvelope ToEnvelope(
        ConversationId conversationId, VisitorId visitorId, bool granted, DateTimeOffset occurredAt, IIdGenerator idGenerator)
    {
        var contract = new AttachmentUploadGrantChanged(
            ConversationId: conversationId.Value,
            VisitorId: visitorId.Value,
            Granted: granted,
            OccurredAt: occurredAt);

        return new EventEnvelope(
            MessageId: idGenerator.NewId(occurredAt),
            Type: nameof(AttachmentUploadGrantChanged),
            Version: 1,
            PartitionKey: contract.ConversationId.ToString(),
            OccurredAt: contract.OccurredAt,
            CorrelationId: idGenerator.NewId(occurredAt),
            Payload: JsonSerializer.Serialize(contract));
    }
}
