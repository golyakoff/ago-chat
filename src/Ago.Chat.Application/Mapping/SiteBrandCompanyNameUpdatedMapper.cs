using System.Text.Json;
using Ago.Chat.Contracts;
using Ago.Chat.Domain;
using Ago.Platform.Abstractions;
using Ago.Platform.Kernel;

namespace Ago.Chat.Application.Mapping;

/// <summary>`25-160`: the identical shape <see cref="SiteWidgetConfigUpdatedMapper"/> already
/// establishes - domain event -> <see cref="SiteSettingsChanged"/> -> <see cref="EventEnvelope"/>, a
/// fresh <see cref="IIdGenerator"/> id for both the contract's own <c>MessageId</c> and the envelope,
/// since <see cref="Site.UpdateBrandCompanyName"/> can run more than once per site.</summary>
public static class SiteBrandCompanyNameUpdatedMapper
{
    public static EventEnvelope ToEnvelope(SiteBrandCompanyNameUpdated domainEvent, IIdGenerator idGenerator)
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
