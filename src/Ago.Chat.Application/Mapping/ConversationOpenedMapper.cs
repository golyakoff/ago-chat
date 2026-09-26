using System.Text.Json;
using Ago.Chat.Contracts;
using Ago.Chat.Domain;
using Ago.Platform.Abstractions;
using Ago.Platform.Kernel;

namespace Ago.Chat.Application.Mapping;

/// <summary>
/// Domain event -> integration event -> <see cref="EventEnvelope"/>, `adr/0186` S1's own repeat of the
/// pattern <see cref="ConversationAssignedToOperatorMapper"/> already established: <paramref name="channel"/>/
/// <paramref name="referrerHost"/>/<paramref name="utmCampaign"/>/<paramref name="tenantZone"/> are not
/// on the domain event itself (<see cref="Domain.ConversationStarted"/> only ever carried the ids and
/// the instant) - <see cref="Application.UseCases.StartConversation.StartConversationHandler"/> already
/// has them (the just-started <see cref="Conversation"/>'s own <see cref="Conversation.Source"/>, a
/// channel-identity lookup, and the cached site config it already reads for
/// <c>WidgetAllowAttachmentUploadsByDefault</c>), so passing them here costs nothing and avoids a second
/// load this mapper has no business doing itself.
///
/// <para>The wire contract is <see cref="Contracts.ConversationOpened"/>, not a same-named
/// <c>ConversationStarted</c> - see that record's own remarks for why the name differs from both the
/// domain event and the design doc's raw-event vocabulary term.</para>
/// </summary>
public static class ConversationOpenedMapper
{
    public static EventEnvelope ToEnvelope(
        ConversationStarted domainEvent, string channel, string? referrerHost, string? utmCampaign,
        string tenantZone, IIdGenerator idGenerator)
    {
        var contract = new ConversationOpened(
            ConversationId: domainEvent.ConversationId.Value,
            SiteId: domainEvent.SiteId.Value,
            VisitorId: domainEvent.VisitorId.Value,
            OccurredAt: domainEvent.OccurredAt,
            CorrelationId: idGenerator.NewId(domainEvent.OccurredAt),
            Channel: channel,
            ReferrerHost: referrerHost,
            UtmCampaign: utmCampaign,
            TenantZone: tenantZone);

        return new EventEnvelope(
            // `1-04`'s "Start() only ever runs once per conversation id" precedent -
            // ConversationClosedMapper's own remarks make the identical argument for its own
            // once-only domain event.
            MessageId: contract.ConversationId,
            Type: nameof(ConversationOpened),
            Version: 1,
            PartitionKey: contract.ConversationId.ToString(),
            OccurredAt: contract.OccurredAt,
            CorrelationId: contract.CorrelationId,
            Payload: JsonSerializer.Serialize(contract));
    }
}
