using System.Text.Json;
using Ago.Chat.Contracts;
using Ago.Chat.Domain;
using Ago.Platform.Abstractions;
using Ago.Platform.Kernel;

namespace Ago.Chat.Application.Mapping;

/// <summary>
/// Domain event -> integration event -> <see cref="EventEnvelope"/>, mirroring
/// <see cref="MessageAcceptedMapper"/>'s exact shape. <see cref="EventEnvelope.MessageId"/> is the
/// attachment's own id, not a fresh one - <c>Attachment.ConfirmReady</c> can only ever run once per
/// attachment (the domain guard rejects a second confirm), so there is exactly one
/// <c>AttachmentConfirmed</c> per attachment, making its id a stable, natural envelope identity - the
/// same role <c>MessageId</c> plays for <c>MessageAccepted</c>.
/// </summary>
public static class AttachmentConfirmedMapper
{
    public static EventEnvelope ToEnvelope(AttachmentReady domainEvent, IIdGenerator idGenerator)
    {
        var contract = new AttachmentConfirmed(
            AttachmentId: domainEvent.AttachmentId.Value,
            OccurredAt: domainEvent.OccurredAt,
            SiteId: domainEvent.SiteId.Value,
            CorrelationId: idGenerator.NewId(domainEvent.OccurredAt),
            ConversationId: domainEvent.ConversationId.Value,
            ObjectKey: domainEvent.ObjectKey,
            ContentType: domainEvent.ContentType);

        return new EventEnvelope(
            MessageId: contract.AttachmentId,
            Type: nameof(AttachmentConfirmed),
            Version: 1,
            PartitionKey: contract.ConversationId.ToString(),
            OccurredAt: contract.OccurredAt,
            CorrelationId: contract.CorrelationId,
            Payload: JsonSerializer.Serialize(contract));
    }
}
