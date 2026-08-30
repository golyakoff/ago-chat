using Ago.Chat.Application.Abstractions;
using Ago.Chat.Domain;

namespace Ago.Chat.Application.Tests.Fakes;

/// <summary>Reads straight from a seeded <see cref="Conversation"/> - standing in for the real
/// Dapper-backed store (`1-04`) without needing SQL to test a handler's access-check logic.</summary>
public sealed class FakeConversationReadStore : IConversationReadStore
{
    private readonly Dictionary<ConversationId, Conversation> _bySource = [];
    private readonly Dictionary<TagId, HashSet<ConversationId>> _taggedBy = [];

    public void Seed(Conversation conversation) => _bySource[conversation.Id] = conversation;

    /// <summary>`18-04`: mirrors the real `conversation_tags` join for
    /// <see cref="GetAllForSiteAsync"/>'s own filter - a test seeds this the same way it seeds a
    /// conversation, without needing a real Postgres.</summary>
    public void SeedTag(ConversationId conversationId, TagId tagId) =>
        (_taggedBy.TryGetValue(tagId, out var set) ? set : _taggedBy[tagId] = []).Add(conversationId);

    public Task<ConversationHistoryPage> GetHistoryAsync(
        ConversationId conversationId, int? beforeSequence, int pageSize, CancellationToken cancellationToken)
    {
        var conversation = _bySource[conversationId];
        var items = conversation.Messages
            .Where(m => beforeSequence is null || m.Sequence < beforeSequence)
            .OrderByDescending(m => m.Sequence)
            .Take(pageSize)
            .Select(ToHistoryItem)
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
            .Select(ToHistoryItem)
            .ToList();

        return Task.FromResult(items);
    }

    /// <summary>`19-01`: fills <see cref="MessageHistoryItem.ContentKind"/>/<see cref="MessageHistoryItem.Payload"/>
    /// from the seeded aggregate's own `14-06` <see cref="Message.Content"/> - previously always left
    /// `null` here regardless of what a test seeded, which made this fake silently unable to prove a
    /// handler's own "skip a structured message" behaviour. Additive only: a message with no
    /// <see cref="Message.Content"/> still maps to `null` exactly as before, so every existing caller
    /// of this fake is unaffected.</summary>
    private static MessageHistoryItem ToHistoryItem(Message m) => new(
        m.Id, m.Sequence, m.AuthorKind, m.AuthorId, m.Body.Value, m.CreatedAt,
        ContentKind: m.Content?.Kind.Value, Payload: m.Content?.Payload?.Value);

    /// <summary>`5-08`: mirrors the real store's keyset shape (id descending, `beforeId` exclusive)
    /// over whatever this fake was seeded with for the requested site - good enough to test a
    /// handler's own access-check and paging-forwarding logic without a real Postgres.</summary>
    public Task<ConversationListPage> GetAllForSiteAsync(
        SiteId siteId, Guid? beforeId, int pageSize, TagId? tagId, CancellationToken cancellationToken)
    {
        var items = _bySource.Values
            .Where(c => c.SiteId == siteId && (beforeId is null || c.Id.Value.CompareTo(beforeId) < 0))
            .Where(c => tagId is null || (_taggedBy.TryGetValue(tagId.Value, out var set) && set.Contains(c.Id)))
            .OrderByDescending(c => c.Id.Value)
            .Take(pageSize)
            .Select(c => new ConversationSummaryItem(
                c.Id, c.VisitorId, c.OperatorId, c.State.ToString(), c.CreatedAt, c.OperatorUnreadCount,
                c.Outcome.ToString()))
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
            conversation.CreatedAt, conversation.OperatorUnreadCount, conversation.Outcome.ToString()));
    }

    /// <summary>`18-07`: mirrors the real store's keyset shape (id descending, `beforeId` exclusive,
    /// current conversation excluded) and its "last message wins the preview" choice - good enough to
    /// test a handler's own access-check, gating and paging-forwarding logic without a real
    /// Postgres.</summary>
    public Task<VisitorHistoryPage> GetVisitorHistoryAsync(
        VisitorId visitorId, ConversationId excludeConversationId, Guid? beforeId, int pageSize,
        CancellationToken cancellationToken)
    {
        var items = _bySource.Values
            .Where(c => c.VisitorId == visitorId && c.Id != excludeConversationId)
            .Where(c => beforeId is null || c.Id.Value.CompareTo(beforeId) < 0)
            .OrderByDescending(c => c.Id.Value)
            .Take(pageSize)
            .Select(c =>
            {
                var lastMessage = c.Messages.OrderByDescending(m => m.Sequence).FirstOrDefault();
                return new VisitorHistoryItem(
                    c.Id,
                    c.State.ToString(),
                    c.CreatedAt,
                    c.ClosedAt,
                    lastMessage?.Body.Value,
                    lastMessage?.AuthorKind,
                    lastMessage?.CreatedAt);
            })
            .ToList();

        var nextCursor = items.Count == pageSize ? items[^1].Id.Value : (Guid?)null;
        return Task.FromResult(new VisitorHistoryPage(items, nextCursor));
    }
}
