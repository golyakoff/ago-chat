using System.Text.Json;
using Ago.Chat.Contracts;
using Ago.Chat.Domain;
using Ago.Platform.Abstractions;
using Ago.Platform.Kernel;

namespace Ago.Chat.Application.Mapping;

/// <summary>
/// `23-59`: builds the <see cref="ContactCollected"/> envelope for both of today's publishers -
/// <c>RecordVisitorContactDetailHandler</c>'s two live entry points, and
/// <c>ContactCarryoverBackfill</c>'s retroactive one - the same "one mapper, not one copy per
/// publisher" reasoning <see cref="RoleAssignmentsChangedMapper"/>'s own remarks give.
///
/// <para><b>Keyed by <see cref="ContactCollected.ContactDetailId"/>, not by site.</b> A contact detail
/// is written once and never edited, so there is no second, later fact about the same id that could
/// ever need to be ordered after this one - unlike <c>RoleAssignmentsChanged</c>, which keys by
/// subject because successive facts about the same person genuinely do need to arrive in order.
/// Keying by the contact's own id here is simply "key by the thing this event is about", with no
/// ordering guarantee being relied on either way.</para>
///
/// <para>Two overloads: one takes a live <see cref="VisitorContactDetail"/> aggregate (the ordinary
/// write path, which already has one in hand), the other takes the same four facts as plain values -
/// what <c>ContactCarryoverBackfill</c> has, reading rows back from Postgres rather than materialising
/// aggregates for a pass that may cover years of history. Both funnel into the same envelope
/// construction so the two publishers can never drift on shape.</para>
/// </summary>
public static class ContactCollectedMapper
{
    public static EventEnvelope ToEnvelope(
        VisitorContactDetail detail, SiteId siteId, DateTimeOffset occurredAt, IIdGenerator idGenerator) =>
        ToEnvelope(
            detail.Id.Value, siteId.Value, detail.Kind.ToString(), detail.Value, detail.RecordedAt, occurredAt,
            idGenerator);

    public static EventEnvelope ToEnvelope(
        Guid contactDetailId,
        Guid siteId,
        string kind,
        string value,
        DateTimeOffset recordedAt,
        DateTimeOffset occurredAt,
        IIdGenerator idGenerator)
    {
        var contract = new ContactCollected(
            ContactDetailId: contactDetailId,
            SiteId: siteId,
            Kind: kind,
            Value: value,
            RecordedAt: recordedAt,
            CorrelationId: idGenerator.NewId(occurredAt),
            OccurredAt: occurredAt);

        return new EventEnvelope(
            MessageId: idGenerator.NewId(occurredAt),
            Type: nameof(ContactCollected),
            Version: 1,
            PartitionKey: contract.ContactDetailId.ToString(),
            OccurredAt: contract.OccurredAt,
            CorrelationId: contract.CorrelationId,
            Payload: JsonSerializer.Serialize(contract));
    }
}
