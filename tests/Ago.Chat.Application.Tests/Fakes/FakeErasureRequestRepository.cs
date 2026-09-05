using Ago.Chat.Application.Abstractions;
using Ago.Chat.Domain;

namespace Ago.Chat.Application.Tests.Fakes;

/// <summary>Mirrors the real <c>ErasureRequestRepository</c>'s own idempotent-`coalesce` contract: a
/// second request for the same site/conversation preserves the first request's timestamp, and a
/// missing site/conversation (or the wrong site for a conversation) returns
/// <see langword="false"/>. `24-13`: also mirrors that only the call which actually sets the flag for
/// the first time "creates" a receipt - tracked here as <see cref="SiteErasureRecordIds"/>/
/// <see cref="ConversationErasureRecordIds"/>, populated only on that first call, the same
/// once-ever-per-request shape the real repository's single atomic statement gives.</summary>
public sealed class FakeErasureRequestRepository : IErasureRequestRepository
{
    private readonly HashSet<SiteId> _existingSites = [];
    private readonly Dictionary<(ConversationId, SiteId), object?> _existingConversations = [];

    public Dictionary<SiteId, DateTimeOffset> SiteErasureRequestedAt { get; } = [];

    public Dictionary<ConversationId, DateTimeOffset> ConversationErasureRequestedAt { get; } = [];

    public Dictionary<SiteId, OperatorId> SiteErasureRequestedBy { get; } = [];

    public Dictionary<ConversationId, OperatorId> ConversationErasureRequestedBy { get; } = [];

    public Dictionary<SiteId, Guid> SiteErasureRecordIds { get; } = [];

    public Dictionary<ConversationId, Guid> ConversationErasureRecordIds { get; } = [];

    public void SeedSite(SiteId siteId) => _existingSites.Add(siteId);

    public void SeedConversation(ConversationId conversationId, SiteId siteId) =>
        _existingConversations[(conversationId, siteId)] = null;

    public Task<bool> RequestSiteErasureAsync(
        SiteId siteId, OperatorId requestedBy, Guid erasureRecordId, DateTimeOffset requestedAt,
        CancellationToken cancellationToken)
    {
        if (!_existingSites.Contains(siteId))
        {
            return Task.FromResult(false);
        }

        if (!SiteErasureRequestedAt.ContainsKey(siteId))
        {
            SiteErasureRequestedAt[siteId] = requestedAt;
            SiteErasureRequestedBy[siteId] = requestedBy;
            SiteErasureRecordIds[siteId] = erasureRecordId;
        }

        return Task.FromResult(true);
    }

    public Task<bool> RequestConversationErasureAsync(
        ConversationId conversationId, SiteId siteId, OperatorId requestedBy, Guid erasureRecordId,
        DateTimeOffset requestedAt, CancellationToken cancellationToken)
    {
        if (!_existingConversations.ContainsKey((conversationId, siteId)))
        {
            return Task.FromResult(false);
        }

        if (!ConversationErasureRequestedAt.ContainsKey(conversationId))
        {
            ConversationErasureRequestedAt[conversationId] = requestedAt;
            ConversationErasureRequestedBy[conversationId] = requestedBy;
            ConversationErasureRecordIds[conversationId] = erasureRecordId;
        }

        return Task.FromResult(true);
    }
}
