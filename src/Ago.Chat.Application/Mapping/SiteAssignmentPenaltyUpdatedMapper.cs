using System.Text.Json;
using Ago.Chat.Contracts;
using Ago.Chat.Domain;
using Ago.Platform.Abstractions;
using Ago.Platform.Kernel;

namespace Ago.Chat.Application.Mapping;

/// <summary>
/// `23-05`: domain event -> integration event -> <see cref="EventEnvelope"/>, the same shape
/// <see cref="SiteOfflineAutoReplyUpdatedMapper"/> established for `14-04` and producing the same
/// <see cref="SiteSettingsChanged"/> contract - see that mapper's own remarks for why every `Site`
/// settings write converges on one contract at the outbox boundary rather than growing a new one
/// per field.
/// </summary>
public static class SiteAssignmentPenaltyUpdatedMapper
{
    public static EventEnvelope ToEnvelope(SiteAssignmentPenaltyUpdated domainEvent, IIdGenerator idGenerator)
    {
        var messageId = idGenerator.NewId(domainEvent.OccurredAt);
        var contract = new SiteSettingsChanged(
            MessageId: messageId,
            OccurredAt: domainEvent.OccurredAt,
            SiteId: domainEvent.SiteId.Value,
            CorrelationId: idGenerator.NewId(domainEvent.OccurredAt),
            PublicKey: domainEvent.PublicKey);

        return new EventEnvelope(
            MessageId: messageId,
            Type: nameof(SiteSettingsChanged),
            Version: 1,
            PartitionKey: contract.SiteId.ToString(),
            OccurredAt: contract.OccurredAt,
            CorrelationId: contract.CorrelationId,
            Payload: JsonSerializer.Serialize(contract));
    }
}
