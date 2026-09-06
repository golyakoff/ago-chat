using System.Text.Json;
using Ago.Chat.Contracts;
using Ago.Platform.Abstractions;
using Ago.Platform.Kernel;

namespace Ago.Chat.Application.Mapping;

/// <summary>
/// `23-33`: builds the <see cref="TeamMessageRemoved"/> envelope for its one publisher,
/// <c>Ago.Chat.Infrastructure.Postgres.TeamChatRepository.RemoveAsync</c> - the same "one mapper, raw
/// values in, no domain event to map from" shape <see cref="TeamMessagePostedMapper"/> already takes,
/// for the identical reason.
/// </summary>
public static class TeamMessageRemovedMapper
{
    public static EventEnvelope ToEnvelope(
        Guid teamMessageId, Guid siteId, int sequence, DateTimeOffset occurredAt, IIdGenerator idGenerator)
    {
        var contract = new TeamMessageRemoved(
            TeamMessageId: teamMessageId,
            OccurredAt: occurredAt,
            SiteId: siteId,
            CorrelationId: idGenerator.NewId(occurredAt),
            Sequence: sequence);

        return new EventEnvelope(
            MessageId: idGenerator.NewId(occurredAt),
            Type: nameof(TeamMessageRemoved),
            Version: 1,
            PartitionKey: contract.SiteId.ToString(),
            OccurredAt: contract.OccurredAt,
            CorrelationId: contract.CorrelationId,
            Payload: JsonSerializer.Serialize(contract));
    }
}
