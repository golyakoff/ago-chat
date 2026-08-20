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
}
