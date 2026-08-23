using Ago.Chat.Domain;

namespace Ago.Chat.Application.Abstractions;

/// <summary>
/// The read-side port: hand-written SQL over the write model, never through the aggregate
/// (adr/0004). Keyset-shaped from the start - <paramref name="beforeSequence"/><c>null</c> means
/// "most recent page."
/// </summary>
public interface IConversationReadStore
{
    Task<ConversationHistoryPage> GetHistoryAsync(
        ConversationId conversationId, int? beforeSequence, int pageSize, CancellationToken cancellationToken);

    /// <summary>
    /// `3-03`'s reconnect delta - every message strictly after <paramref name="afterSequence"/>,
    /// oldest first. Unbounded rather than keyset-paginated like <see cref="GetHistoryAsync"/>: the
    /// gap this closes is bounded by how long *one* client was disconnected, not by the
    /// conversation's whole history, so it does not carry the unbounded-result risk that
    /// <see cref="GetHistoryAsync"/>'s "load older messages" direction guards against by paging.
    /// </summary>
    Task<IReadOnlyList<MessageHistoryItem>> GetDeltaAsync(
        ConversationId conversationId, int afterSequence, CancellationToken cancellationToken);

    /// <summary>
    /// `5-08`: the admin/supervisor view's own read - every conversation for a site regardless of
    /// state or assignment, unlike <see cref="IConversationRepository.GetWaitingForSiteAsync"/>/
    /// <see cref="IConversationRepository.GetAssignedToOperatorAsync"/> (bounded, state-filtered
    /// lists small enough that going through the write-side EF repository was the right call - see
    /// that interface's own remarks). A site's full conversation history carries no such bound; it
    /// only grows, which is exactly the "paginated, potentially-large read" case this read store
    /// exists for, so this lives here rather than as a third method on the write-side repository.
    /// </summary>
    Task<ConversationListPage> GetAllForSiteAsync(
        SiteId siteId, Guid? beforeId, int pageSize, CancellationToken cancellationToken);
}
