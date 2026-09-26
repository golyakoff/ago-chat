using Ago.Chat.Application.Abstractions;
using Ago.Chat.Domain;

namespace Ago.Chat.Application.Tests.Fakes;

public sealed class FakeTagRepository : ITagRepository
{
    private readonly Dictionary<TagId, Tag> _byId = [];
    private readonly Dictionary<(ConversationId ConversationId, TagId TagId), TagSource> _associations = [];

    /// <summary>`adr/0186` S1: mirrors what the real <c>TagRepository</c> adapter would have staged to
    /// the outbox - one entry per call that actually changed something, none for a no-op. Stands in for
    /// the real adapter's own <c>IOutboxWriter</c> so a handler-level test (which only ever sees this
    /// fake, never the real Postgres-backed adapter) can still assert the analytics-relevant fact was
    /// recorded with the right ids, without pulling in a Testcontainers-backed integration test just to
    /// prove parameter plumbing.</summary>
    public List<(ConversationId ConversationId, SiteId SiteId, TagId TagId, DateTimeOffset Now)> Tagged { get; } = [];

    /// <summary>The mirror list for <see cref="RemoveFromConversationAsync"/>.</summary>
    public List<(ConversationId ConversationId, SiteId SiteId, TagId TagId, DateTimeOffset Now)> Untagged { get; } = [];

    public void Seed(Tag tag) => _byId[tag.Id] = tag;

    /// <summary>Defaults to <see cref="TagSource.Operator"/> - every existing caller of this fake seeds
    /// an operator-applied association, unchanged by `19-02`'s own addition below.</summary>
    public void SeedAssociation(ConversationId conversationId, TagId tagId, TagSource source = TagSource.Operator) =>
        _associations[(conversationId, tagId)] = source;

    public Task<Tag?> GetByIdAsync(TagId id, SiteId siteId, CancellationToken cancellationToken)
    {
        var tag = _byId.GetValueOrDefault(id);
        return Task.FromResult(tag is not null && tag.SiteId == siteId ? tag : null);
    }

    public Task<Tag?> GetByNameAsync(SiteId siteId, string name, CancellationToken cancellationToken) =>
        Task.FromResult(_byId.Values.FirstOrDefault(
            t => t.SiteId == siteId && string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase)));

    public Task<IReadOnlyList<Tag>> GetAllForSiteAsync(SiteId siteId, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<Tag>>(_byId.Values.Where(t => t.SiteId == siteId).OrderBy(t => t.Name).ToList());

    public Task SaveAsync(Tag tag, CancellationToken cancellationToken)
    {
        if (_byId.Values.Any(t => t.Id != tag.Id && t.SiteId == tag.SiteId
            && string.Equals(t.Name, tag.Name, StringComparison.Ordinal)))
        {
            throw new TagNameConflictException($"A tag named '{tag.Name}' already exists for this site.");
        }

        _byId[tag.Id] = tag;
        return Task.CompletedTask;
    }

    public Task DeleteAsync(Tag tag, CancellationToken cancellationToken)
    {
        _byId.Remove(tag.Id);
        foreach (var key in _associations.Keys.Where(a => a.TagId == tag.Id).ToList())
        {
            _associations.Remove(key);
        }

        return Task.CompletedTask;
    }

    public Task AddToConversationAsync(
        ConversationId conversationId, SiteId siteId, TagId tagId, TagSource source, DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        // `19-02`: mirrors the real adapter's own ON CONFLICT DO NOTHING - a second write for an
        // already-associated pair never overwrites the first writer's source (TagRepository's own
        // remarks on why that is the correct outcome either way round).
        if (!_associations.ContainsKey((conversationId, tagId)))
        {
            _associations[(conversationId, tagId)] = source;
            // `adr/0186` S1: only on a real change - the identical "never publish for a no-op" guard
            // the real adapter's own remarks state, mirrored here.
            Tagged.Add((conversationId, siteId, tagId, now));
        }

        return Task.CompletedTask;
    }

    public Task RemoveFromConversationAsync(
        ConversationId conversationId, SiteId siteId, TagId tagId, DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        if (_associations.Remove((conversationId, tagId)))
        {
            Untagged.Add((conversationId, siteId, tagId, now));
        }

        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<ConversationTagEntry>> GetForConversationAsync(
        ConversationId conversationId, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<ConversationTagEntry>>(_associations
            .Where(a => a.Key.ConversationId == conversationId)
            .Select(a => new ConversationTagEntry(_byId[a.Key.TagId], a.Value))
            .OrderBy(e => e.Tag.Name)
            .ToList());

    public Task<IReadOnlySet<ConversationId>> GetConversationIdsForTagAsync(
        TagId tagId, SiteId siteId, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlySet<ConversationId>>(_associations.Keys
            .Where(a => a.TagId == tagId && _byId.GetValueOrDefault(a.TagId)?.SiteId == siteId)
            .Select(a => a.ConversationId)
            .ToHashSet());
}
