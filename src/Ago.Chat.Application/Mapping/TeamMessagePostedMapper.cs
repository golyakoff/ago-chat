using System.Text.Json;
using Ago.Chat.Contracts;
using Ago.Platform.Abstractions;
using Ago.Platform.Kernel;

namespace Ago.Chat.Application.Mapping;

/// <summary>
/// `23-32`: builds the <see cref="TeamMessagePosted"/> envelope for its one publisher,
/// <c>Ago.Chat.Infrastructure.Postgres.TeamChatRepository</c> - the same "one mapper, raw values in,
/// no domain event to map from" shape <see cref="ModuleQuantityGrantedMapper"/> already takes,
/// because <c>Ago.Chat.Domain.TeamMessage</c> has no aggregate root to raise one from (its own
/// remarks explain why).
///
/// <para>Keyed by <paramref name="siteId"/> - the room's own ordering key, the same
/// <see cref="EventEnvelope.PartitionKey"/> role <see cref="MessageAcceptedMapper"/> gives
/// <c>ConversationId</c> for an ordinary message: two posts to the same room must not be processed
/// out of order by a competing consumer, and nothing about this event needs ordering against any
/// other room's.</para>
/// </summary>
public static class TeamMessagePostedMapper
{
    public static EventEnvelope ToEnvelope(
        Guid teamMessageId, Guid siteId, int sequence, DateTimeOffset occurredAt, IIdGenerator idGenerator)
    {
        var contract = new TeamMessagePosted(
            TeamMessageId: teamMessageId,
            OccurredAt: occurredAt,
            SiteId: siteId,
            CorrelationId: idGenerator.NewId(occurredAt),
            Sequence: sequence);

        return new EventEnvelope(
            MessageId: teamMessageId,
            Type: nameof(TeamMessagePosted),
            Version: 1,
            PartitionKey: contract.SiteId.ToString(),
            OccurredAt: contract.OccurredAt,
            CorrelationId: contract.CorrelationId,
            Payload: JsonSerializer.Serialize(contract));
    }
}
