using Ago.Chat.Application.Abstractions;
using Ago.Chat.Domain;

namespace Ago.Chat.Application.Tests.Fakes;

/// <summary>Mirrors the real <c>ErasureRequestRepository</c>'s own idempotent-`coalesce` contract: a
/// second request for the same site/conversation preserves the first request's timestamp, and a
/// missing site/conversation (or the wrong site for a conversation) returns
/// <see langword="false"/>.</summary>
public sealed class FakeErasureRequestRepository : IErasureRequestRepository
{
    private readonly HashSet<SiteId> _existingSites = [];
    private readonly Dictionary<(ConversationId, SiteId), object?> _existingConversations = [];

    public Dictionary<SiteId, DateTimeOffset> SiteErasureRequestedAt { get; } = [];

    public Dictionary<ConversationId, DateTimeOffset> ConversationErasureRequestedAt { get; } = [];

    public void SeedSite(SiteId siteId) => _existingSites.Add(siteId);

    public void SeedConversation(ConversationId conversationId, SiteId siteId) =>
        _existingConversations[(conversationId, siteId)] = null;

    public Task<bool> RequestSiteErasureAsync(SiteId siteId, DateTimeOffset requestedAt, CancellationToken cancellationToken)
    {
        if (!_existingSites.Contains(siteId))
        {
            return Task.FromResult(false);
        }

        if (!SiteErasureRequestedAt.ContainsKey(siteId))
        {
            SiteErasureRequestedAt[siteId] = requestedAt;
        }

        return Task.FromResult(true);
    }

    public Task<bool> RequestConversationErasureAsync(
        ConversationId conversationId, SiteId siteId, DateTimeOffset requestedAt, CancellationToken cancellationToken)
    {
        if (!_existingConversations.ContainsKey((conversationId, siteId)))
        {
            return Task.FromResult(false);
        }

        if (!ConversationErasureRequestedAt.ContainsKey(conversationId))
        {
            ConversationErasureRequestedAt[conversationId] = requestedAt;
        }

        return Task.FromResult(true);
    }
}
