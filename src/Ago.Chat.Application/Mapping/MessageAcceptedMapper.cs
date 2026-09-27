using System.Text.Json;
using Ago.Chat.Contracts;
using Ago.Chat.Domain;
using Ago.Platform.Abstractions;
using Ago.Platform.Kernel;

namespace Ago.Chat.Application.Mapping;

/// <summary>
/// Domain event -> integration event -> <see cref="EventEnvelope"/>, in one place - the only place
/// <c>Ago.Chat.Domain.MessageAdded</c> and <c>Ago.Chat.Contracts.MessageAccepted</c> meet
/// (`clean-architecture.md`: mapping happens in Application when writing to the outbox, never a
/// shared type between Domain and Contracts).
///
/// <para><b>`26-215`: <paramref name="tenantZone"/> joins this mapper</b> - <c>MessageAdded</c> itself
/// carries no site time zone (Domain is not allowed to know one, rule 1), so every one of this event's
/// several callers resolves it the same way <see cref="ConversationOpenedMapper"/>'s own caller already
/// does (a cached <c>GetSiteConfigByIdHandler</c>/<c>SiteConfigDto</c> read, several of them already
/// held for another reason at the call site) and hands it in here.</para>
/// </summary>
public static class MessageAcceptedMapper
{
    public static EventEnvelope ToEnvelope(MessageAdded domainEvent, string tenantZone, IIdGenerator idGenerator)
    {
        var contract = new MessageAccepted(
            MessageId: domainEvent.MessageId.Value,
            OccurredAt: domainEvent.OccurredAt,
            SiteId: domainEvent.SiteId.Value,
            // No request-tracing correlation id exists to thread through yet (Stage 7) - a fresh id
            // per event is the honest choice today, not a borrowed one that would imply a link that
            // isn't actually there.
            CorrelationId: idGenerator.NewId(domainEvent.OccurredAt),
            ConversationId: domainEvent.ConversationId.Value,
            AuthorKind: domainEvent.AuthorKind.ToString(),
            Sequence: domainEvent.Sequence,
            TenantZone: tenantZone);

        return new EventEnvelope(
            MessageId: contract.MessageId,
            Type: nameof(MessageAccepted),
            Version: 1,
            PartitionKey: contract.ConversationId.ToString(),
            OccurredAt: contract.OccurredAt,
            CorrelationId: contract.CorrelationId,
            Payload: JsonSerializer.Serialize(contract));
    }
}
