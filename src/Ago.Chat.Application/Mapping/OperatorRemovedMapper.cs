using System.Text.Json;
using Ago.Chat.Contracts;
using Ago.Chat.Domain;
using Ago.Platform.Abstractions;
using Ago.Platform.Kernel;

namespace Ago.Chat.Application.Mapping;

/// <summary>
/// `13-03`: <c>Ago.Chat.Domain.OperatorRemoved</c> -&gt; <see cref="OperatorRemovedFromSite"/> -&gt;
/// <see cref="EventEnvelope"/>, the same "mapping happens in Application when writing to the outbox"
/// shape every other mapper in this folder already establishes. A fresh <see cref="IIdGenerator"/> id
/// for the envelope's own <c>MessageId</c>, not <see cref="OperatorId"/> reused - the identical reason
/// <see cref="ConversationAssignedToOperatorMapper"/>'s own remarks give: this domain event carries no
/// entity id of its own that only ever fires once per row (<c>Operator.Remove</c> can only ever run
/// once per operator, so reusing <c>OperatorId</c> would in fact be safe here, but a fresh id keeps
/// every mapper in this folder following one uniform rule rather than a case-by-case exception).
/// </summary>
public static class OperatorRemovedMapper
{
    public static EventEnvelope ToEnvelope(OperatorRemoved domainEvent, IIdGenerator idGenerator)
    {
        var messageId = idGenerator.NewId(domainEvent.OccurredAt);
        var contract = new OperatorRemovedFromSite(
            OperatorId: domainEvent.OperatorId.Value,
            SiteId: domainEvent.SiteId.Value,
            CorrelationId: idGenerator.NewId(domainEvent.OccurredAt),
            OccurredAt: domainEvent.OccurredAt);

        return new EventEnvelope(
            MessageId: messageId,
            Type: nameof(OperatorRemovedFromSite),
            Version: 1,
            PartitionKey: contract.OperatorId.ToString(),
            OccurredAt: contract.OccurredAt,
            CorrelationId: contract.CorrelationId,
            Payload: JsonSerializer.Serialize(contract));
    }
}
