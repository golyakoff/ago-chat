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
}
