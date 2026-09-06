using Ago.Chat.Domain;

namespace Ago.Chat.Application.Abstractions;

/// <summary>
/// `23-32`: the team chat's own read - hand-written SQL over the write model, never through
/// <see cref="Ago.Chat.Domain.TeamMessage"/> (adr/0004), the identical write/read split
/// <see cref="IConversationReadStore"/> already draws for <c>messages</c>. Scoped by
/// <see cref="SiteId"/> alone, not by a conversation - a team chat has exactly one room per site,
/// so there is no narrower key to filter on.
/// </summary>
public interface ITeamMessageReadStore
{
    /// <summary>A keyset page, newest first (data-model.md: <c>OFFSET</c> is banned) -
    /// <paramref name="beforeSequence"/> <see langword="null"/> means "most recent page," the same
    /// convention <see cref="IConversationReadStore.GetHistoryAsync"/> uses.</summary>
    Task<TeamMessageHistoryPage> GetHistoryAsync(
        SiteId siteId, int? beforeSequence, int pageSize, CancellationToken cancellationToken);

    /// <summary>`3-03`'s reconnect delta, the team-chat sibling of
    /// <see cref="IConversationReadStore.GetDeltaAsync"/> - every message strictly after
    /// <paramref name="afterSequence"/>, oldest first, unbounded rather than keyset-paginated for the
    /// identical reason that method states for itself: the gap is bounded by how long one client was
    /// disconnected, not by the room's whole history.</summary>
    Task<IReadOnlyList<TeamMessageHistoryItem>> GetDeltaAsync(
        SiteId siteId, int afterSequence, CancellationToken cancellationToken);

    /// <summary>One message by its own sequence - the local-echo read <c>OperatorHub</c> makes right
    /// after <c>SendTeamMessageHandler</c> returns, the same "re-read the just-written row so every
    /// delivery of a message is byte-identical" shape <c>OperatorHub.SendAsync</c> already uses for an
    /// ordinary conversation message. <see langword="null"/> if the sequence does not exist for this
    /// site - not expected in practice, since this is only ever called with a sequence
    /// <see cref="Application.Abstractions.ITeamChatRepository.PostAsync"/> just assigned.</summary>
    Task<TeamMessageHistoryItem?> GetBySequenceAsync(SiteId siteId, int sequence, CancellationToken cancellationToken);
}

/// <summary>One row of a team chat read, joined against the author's current <c>operators</c> row for
/// its display name/email - see <c>Ago.Chat.Contracts.TeamMessageDto</c>'s own remarks on why that
/// join happens at read time rather than the name being stamped onto the message.
///
/// <para>`23-33`: <see cref="Body"/> is <see langword="null"/> exactly when <see cref="RemovedAt"/> is
/// not - the read store itself nulls it out (never the write side - <c>Ago.Chat.Domain.TeamMessage</c>'s
/// own remarks on why the original text stays at rest, unscrubbed), so a removed message's content
/// never crosses out of <c>Ago.Chat.Infrastructure.Postgres</c> at all, not even as far as this
/// record.</para>
/// </summary>
public sealed record TeamMessageHistoryItem(
    TeamMessageId Id,
    int Sequence,
    OperatorId AuthorOperatorId,
    string? AuthorDisplayName,
    string? AuthorEmail,
    bool AuthorIsAdmin,
    string? Body,
    DateTimeOffset CreatedAt,
    Guid? ClientMessageId,
    DateTimeOffset? RemovedAt = null);

public sealed record TeamMessageHistoryPage(IReadOnlyList<TeamMessageHistoryItem> Messages, int? NextBeforeSequence);
