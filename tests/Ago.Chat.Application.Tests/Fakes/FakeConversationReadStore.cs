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

    /// <summary>`5-08`: mirrors the real store's keyset shape (id descending, `beforeId` exclusive)
    /// over whatever this fake was seeded with for the requested site - good enough to test a
    /// handler's own access-check and paging-forwarding logic without a real Postgres.</summary>
    public Task<ConversationListPage> GetAllForSiteAsync(
        SiteId siteId, Guid? beforeId, int pageSize, CancellationToken cancellationToken)
    {
        var items = _bySource.Values
            .Where(c => c.SiteId == siteId && (beforeId is null || c.Id.Value.CompareTo(beforeId) < 0))
            .OrderByDescending(c => c.Id.Value)
            .Take(pageSize)
            .Select(c => new ConversationSummaryItem(
                c.Id, c.VisitorId, c.OperatorId, c.State.ToString(), c.CreatedAt, c.OperatorUnreadCount))
            .ToList();

        var nextCursor = items.Count == pageSize ? items[^1].Id.Value : (Guid?)null;
        return Task.FromResult(new ConversationListPage(items, nextCursor));
    }

    /// <summary>`16-02`: the point-lookup counterpart to <see cref="GetAllForSiteAsync"/> above - the
    /// same site-scoped filter, no paging.</summary>
    public Task<ConversationSummaryItem?> GetByIdAsync(
        ConversationId conversationId, SiteId siteId, CancellationToken cancellationToken)
    {
        if (!_bySource.TryGetValue(conversationId, out var conversation) || conversation.SiteId != siteId)
        {
            return Task.FromResult<ConversationSummaryItem?>(null);
        }

        return Task.FromResult<ConversationSummaryItem?>(new ConversationSummaryItem(
            conversation.Id, conversation.VisitorId, conversation.OperatorId, conversation.State.ToString(),
            conversation.CreatedAt, conversation.OperatorUnreadCount));
    }
}
