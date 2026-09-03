using System.Text.Json;
using Ago.Chat.Contracts;
using Ago.Platform.Abstractions;
using Ago.Platform.Kernel;

namespace Ago.Chat.Application.Mapping;

/// <summary>
/// `22-05`: builds the <see cref="RoleAssignmentsChanged"/> envelope for all three of today's
/// publishers - site registration and invite redemption (`Ago.Chat.Infrastructure.Postgres`, which may
/// reference `Application` per the dependency rule) and operator removal
/// (`Ago.Chat.Application.UseCases.RemoveOperator.RemoveOperatorHandler`). One mapper rather than three
/// copies, for the same reason every other mapper in this folder exists: the envelope's shape
/// (<see cref="EventEnvelope.PartitionKey"/>'s value, the topic name, the version) is a decision made
/// once, not re-derived at each call site.
///
/// <para><b>Keyed by <see cref="RoleAssignmentsChanged.ExternalSubjectId"/>, not by site.</b> The only
/// ordering that matters is between successive facts about the same person - two different operators'
/// role changes on the same site have no ordering relationship worth serialising them for, and keying
/// by site would make a busy site's onboarding queue up behind itself for a guarantee nobody
/// needs (the same reasoning `messaging.md` gives for keying `BookingConfirmed` per booking rather than
/// per tenant).</para>
/// </summary>
public static class RoleAssignmentsChangedMapper
{
    public static EventEnvelope ToEnvelope(
        string externalSubjectId,
        Guid siteId,
        IReadOnlyList<string> permissions,
        DateTimeOffset occurredAt,
        IIdGenerator idGenerator)
    {
        var contract = new RoleAssignmentsChanged(
            ExternalSubjectId: externalSubjectId,
            SiteId: siteId,
            Permissions: permissions,
            CorrelationId: idGenerator.NewId(occurredAt),
            OccurredAt: occurredAt);

        return new EventEnvelope(
            MessageId: idGenerator.NewId(occurredAt),
            Type: nameof(RoleAssignmentsChanged),
            Version: 1,
            PartitionKey: contract.ExternalSubjectId,
            OccurredAt: contract.OccurredAt,
            CorrelationId: contract.CorrelationId,
            Payload: JsonSerializer.Serialize(contract));
    }
}
