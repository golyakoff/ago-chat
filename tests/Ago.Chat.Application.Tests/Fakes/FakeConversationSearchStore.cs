using Ago.Chat.Application.Abstractions;
using Ago.Chat.Domain;

namespace Ago.Chat.Application.Tests.Fakes;

/// <summary>Records the exact bound `SearchConversationsHandler` resolved and hands back whatever this
/// fake was seeded with - good enough to test the handler's own access-check, phrase/range validation
/// and bound-defaulting logic without a real Postgres full-text query.</summary>
public sealed class FakeConversationSearchStore : IConversationSearchStore
{
    private readonly List<ConversationSearchResultItem> _seeded = [];

    public SiteId? LastSiteId { get; private set; }

    public string? LastPhrase { get; private set; }

    public DateTimeOffset? LastFrom { get; private set; }

    public DateTimeOffset? LastTo { get; private set; }

    public void Seed(ConversationSearchResultItem item) => _seeded.Add(item);

    public Task<ConversationSearchPage> SearchAsync(
        SiteId siteId, string phrase, DateTimeOffset from, DateTimeOffset to, Guid? beforeMessageId, int pageSize,
        CancellationToken cancellationToken)
    {
        LastSiteId = siteId;
        LastPhrase = phrase;
        LastFrom = from;
        LastTo = to;

        var items = _seeded
            .Where(i => i.CreatedAt >= from && i.CreatedAt < to)
            .OrderByDescending(i => i.MessageId.Value)
            .Take(pageSize)
            .ToList();

        return Task.FromResult(new ConversationSearchPage(items, null));
    }
}
