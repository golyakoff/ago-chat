using System.Text.Json;
using Ago.Chat.Contracts;
using Ago.Chat.Domain;
using Ago.Platform.Abstractions;
using Ago.Platform.Kernel;

namespace Ago.Chat.Application.Mapping;

/// <summary>
/// `22-08`/`adr/0149` rule 1: the cross-product mapping of <see cref="SiteSuspensionChanged"/> - the
/// one contract `ago-calendar`'s own <c>TenantSuspensionChangedConsumer</c> reads, the same role
/// <see cref="ContactVisibilityChangedMapper"/> plays for the contact-visibility rung.
///
/// <para><b>Two call sites, one for each of two different reasons.</b> A one-shot publish from
/// <c>SuspendTenantAsOwnerHandler</c>/<c>ExtendSuspensionAsOwnerHandler</c>/<c>LiftSuspensionAsOwnerHandler</c>,
/// mapping the domain event those handlers' own writes raise - and a second, periodic publish from
/// <c>Ago.Chat.Worker.SuspensionLeaseRenewalJob</c>, which raises no domain event at all (nothing about
/// <see cref="Site"/> itself changes on a renewal tick) and instead builds the identical envelope shape
/// directly from a currently-suspended site's own id and a freshly computed lease instant. Both calls
/// converge on this one mapper so the wire shape is decided once.</para>
///
/// <para><b>Keyed by <see cref="TenantSuspensionChanged.SiteId"/>, not by an external subject</b> - the
/// identical reasoning <see cref="ContactVisibilityChangedMapper"/>'s own remarks give: the only
/// ordering that matters is between successive facts about the same site's own suspension state,
/// which <see cref="EventEnvelope.PartitionKey"/> = site id already guarantees (rule 6).</para>
/// </summary>
public static class TenantSuspensionChangedMapper
{
    public static EventEnvelope ToEnvelope(
        SiteId siteId, DateTimeOffset? suspendedUntil, DateTimeOffset occurredAt, IIdGenerator idGenerator)
    {
        var contract = new TenantSuspensionChanged(
            SiteId: siteId.Value,
            SuspendedUntil: suspendedUntil,
            CorrelationId: idGenerator.NewId(occurredAt),
            OccurredAt: occurredAt);

        return new EventEnvelope(
            MessageId: idGenerator.NewId(occurredAt),
            Type: nameof(TenantSuspensionChanged),
            Version: 1,
            PartitionKey: contract.SiteId.ToString(),
            OccurredAt: contract.OccurredAt,
            CorrelationId: contract.CorrelationId,
            Payload: JsonSerializer.Serialize(contract));
    }
}
