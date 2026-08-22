using Ago.Chat.Application.Abstractions;
using Ago.Chat.Domain;

namespace Ago.Chat.Application.Tests.Fakes;

/// <summary>Reads straight from a seeded <see cref="Conversation"/> - standing in for the real
/// Dapper-backed store (`1-04`) without needing SQL to test a handler's access-check logic.</summary>
public sealed class FakeConversationReadStore : IConversationReadStore
{
    private readonly Dictionary<ConversationId, Conversation> _bySource = [];

    public void Seed(Conversation conversation) => _bySource[conversation.Id] = conversation;

    public Task<ConversationHistoryPage> GetHistoryAsync(
        ConversationId conversationId, int? beforeSequence, int pageSize, CancellationToken cancellationToken)
    {
        var conversation = _bySource[conversationId];
        var items = conversation.Messages
            .Where(m => beforeSequence is null || m.Sequence < beforeSequence)
            .OrderByDescending(m => m.Sequence)
            .Take(pageSize)
            .Select(m => new MessageHistoryItem(m.Id, m.Sequence, m.AuthorKind, m.AuthorId, m.Body.Value, m.CreatedAt))
            .ToList();

        var nextCursor = items.Count == pageSize ? items[^1].Sequence : (int?)null;
        return Task.FromResult(new ConversationHistoryPage(items, nextCursor));
    }

    public Task<IReadOnlyList<MessageHistoryItem>> GetDeltaAsync(
        ConversationId conversationId, int afterSequence, CancellationToken cancellationToken)
    {
        var conversation = _bySource[conversationId];
        IReadOnlyList<MessageHistoryItem> items = conversation.Messages
            .Where(m => m.Sequence > afterSequence)
            .OrderBy(m => m.Sequence)
            .Select(m => new MessageHistoryItem(m.Id, m.Sequence, m.AuthorKind, m.AuthorId, m.Body.Value, m.CreatedAt))
            .ToList();

        return Task.FromResult(items);
    }
}
