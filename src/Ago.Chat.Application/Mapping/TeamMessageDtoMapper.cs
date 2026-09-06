using Ago.Chat.Application.Abstractions;
using Ago.Chat.Contracts;

namespace Ago.Chat.Application.Mapping;

/// <summary>The team-chat sibling of <see cref="MessageDtoMapper"/> - one read-model row to one wire
/// message, kept in its own place for the identical reason: a fan-out copy and a local-echo copy of
/// the same message disagreeing is `5-11`'s own failure mode, found live once already for ordinary
/// conversation messages.</summary>
public static class TeamMessageDtoMapper
{
    public static TeamMessageDto ToDto(TeamMessageHistoryItem item)
    {
        ArgumentNullException.ThrowIfNull(item);

        return new TeamMessageDto(
            item.Id.Value,
            item.Sequence,
            item.AuthorOperatorId.Value,
            item.AuthorDisplayName,
            item.AuthorEmail,
            item.AuthorIsAdmin,
            item.Body,
            item.CreatedAt,
            item.ClientMessageId);
    }

    public static IReadOnlyList<TeamMessageDto> ToDtos(IReadOnlyList<TeamMessageHistoryItem> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        return [.. items.Select(ToDto)];
    }
}
