using System.Text.Json;
using Ago.Chat.Contracts;
using Ago.Chat.Domain;
using Ago.Platform.Abstractions;
using Ago.Platform.Kernel;

namespace Ago.Chat.Application.Mapping;

/// <summary>
/// `13-02`: the fourth mapper converging on <see cref="SiteSettingsChanged"/> - see
/// <see cref="SiteWidgetConfigUpdatedMapper"/>'s own remarks for why a domain event maps to this shared
/// contract rather than a dedicated `SiteSubscriptionActivated` integration event. `Site.ActivateSubscription`
/// can run more than once per site (`13-03`'s future renewal path will call it again), so - the same
/// reasoning `SiteWidgetConfigUpdatedMapper` already gives - the envelope's own `MessageId` is a fresh id
/// from <see cref="IIdGenerator"/>, not a reused domain identity.
/// </summary>
public static class SiteSubscriptionActivatedMapper
{
    public static EventEnvelope ToEnvelope(SiteSubscriptionActivated domainEvent, IIdGenerator idGenerator)
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
