using System.Text.Json;
using Ago.Chat.Contracts;
using Ago.Chat.Domain;
using Ago.Platform.Abstractions;
using Ago.Platform.Kernel;

namespace Ago.Chat.Application.Mapping;

/// <summary>
/// `11-10`: domain event -> integration event -> <see cref="EventEnvelope"/>, the same shape
/// <see cref="SiteWidgetConfigUpdatedMapper"/>/<see cref="SiteOfflineAutoReplyUpdatedMapper"/> already
/// established, and producing the same <see cref="SiteSettingsChanged"/> contract - the third mapper
/// to converge on it, for the reason <see cref="SiteOfflineAutoReplyUpdatedMapper"/>'s own remarks
/// give: the convergence happens at the outbox boundary, not before it, so the domain stays able to
/// tell a locale change apart from an appearance change while a cache invalidator that cares about
/// neither in particular still sees one fact.
///
/// A fresh <see cref="IIdGenerator"/> id for both the contract's <c>MessageId</c> and the envelope's,
/// for the identical reason the other two mappers give: a site's locale can change any number of
/// times, so there is no once-only natural identity to reuse.
/// </summary>
public static class SiteLocaleUpdatedMapper
{
    public static EventEnvelope ToEnvelope(SiteLocaleUpdated domainEvent, IIdGenerator idGenerator)
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
