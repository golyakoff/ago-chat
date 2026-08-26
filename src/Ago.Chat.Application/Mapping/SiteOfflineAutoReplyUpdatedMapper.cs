using System.Text.Json;
using Ago.Chat.Contracts;
using Ago.Chat.Domain;
using Ago.Platform.Abstractions;
using Ago.Platform.Kernel;

namespace Ago.Chat.Application.Mapping;

/// <summary>
/// `14-04`: domain event -> integration event -> <see cref="EventEnvelope"/>, the same shape
/// <see cref="SiteWidgetConfigUpdatedMapper"/> established for `11-01`, and producing the same
/// <see cref="SiteSettingsChanged"/> contract.
///
/// <para><b>Why two mappers onto one contract, rather than one mapper taking both events.</b> The
/// convergence is deliberate and it happens here, at the outbox boundary, not earlier: a cache
/// invalidation cares only that this site's settings changed, so one integration event is the honest
/// shape for the consumer. Inside the domain the two changes stay distinguishable
/// (<see cref="SiteOfflineAutoReplyUpdated"/>'s own remarks), which is what a future consumer that
/// genuinely needs to tell them apart will need. Keeping the mapping one-per-domain-event means
/// splitting the contract later is a change to this file, not an unpicking of a shared one.</para>
///
/// <para>A fresh <see cref="IIdGenerator"/> id for both the contract's <c>MessageId</c> and the
/// envelope's, for the identical reason `11-01` gave: a site's settings can change any number of
/// times, so there is no once-only natural identity to reuse.</para>
/// </summary>
public static class SiteOfflineAutoReplyUpdatedMapper
{
    public static EventEnvelope ToEnvelope(SiteOfflineAutoReplyUpdated domainEvent, IIdGenerator idGenerator)
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
