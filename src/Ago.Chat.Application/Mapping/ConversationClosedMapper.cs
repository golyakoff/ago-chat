using System.Text.Json;
using Ago.Chat.Contracts;
using Ago.Chat.Domain;
using Ago.Platform.Abstractions;
using Ago.Platform.Kernel;

namespace Ago.Chat.Application.Mapping;

/// <summary>
/// Domain event -> integration event -> <see cref="EventEnvelope"/>, `6-02`'s own repeat of the
/// pattern <see cref="ConversationAssignedToOperatorMapper"/>/<see cref="ConversationReleasedToQueueMapper"/>
/// already established (clean-architecture.md: mapping happens in Application when writing to the
/// outbox, never a shared type between Domain and Contracts). The wire type is
/// <see cref="Contracts.ConversationEnded"/>, not a same-named <c>ConversationClosed</c> - see its own
/// remarks for why the record has to be named differently from <see cref="Domain.ConversationClosed"/>
/// even though this mapper class itself, in a third namespace, has no name collision of its own to
/// avoid.
///
/// <see cref="EventEnvelope.MessageId"/> reuses the conversation's own id, not a fresh one -
/// <see cref="Conversation.Close"/> can only ever run once per conversation (the domain guard rejects
/// closing an already-closed one), so there is exactly one <c>ConversationClosed</c> per conversation,
/// making its id a stable, natural envelope identity - the same role it plays for
/// <see cref="AttachmentConfirmedMapper"/>'s similarly once-only <c>AttachmentReady</c>.
///
/// <para><b>`26-215`: <paramref name="siteId"/>/<paramref name="tenantZone"/> join this mapper</b> -
/// <see cref="Domain.ConversationClosed"/> itself only ever carried the conversation id and the instant
/// (Domain is not allowed to know a site's own time zone, rule 1), so every caller now resolves both
/// the same way <see cref="ConversationOpenedMapper"/>'s own callers already do and hands them in here,
/// exactly as <see cref="Contracts.ConversationEnded"/>'s own remarks describe.</para>
/// </summary>
public static class ConversationClosedMapper
{
    public static EventEnvelope ToEnvelope(
        ConversationClosed domainEvent, Guid siteId, string tenantZone, IIdGenerator idGenerator)
    {
        var contract = new ConversationEnded(
            ConversationId: domainEvent.ConversationId.Value,
            ClosedAt: domainEvent.OccurredAt,
            SiteId: siteId,
            TenantZone: tenantZone);

        return new EventEnvelope(
            MessageId: contract.ConversationId,
            Type: nameof(ConversationEnded),
            Version: 1,
            PartitionKey: contract.ConversationId.ToString(),
            OccurredAt: contract.ClosedAt,
            CorrelationId: idGenerator.NewId(domainEvent.OccurredAt),
            Payload: JsonSerializer.Serialize(contract));
    }
}
