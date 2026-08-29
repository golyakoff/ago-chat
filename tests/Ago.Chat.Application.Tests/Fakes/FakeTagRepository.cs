using Ago.Chat.Application.Abstractions;
using Ago.Chat.Domain;

namespace Ago.Chat.Application.Tests.Fakes;

public sealed class FakeTagRepository : ITagRepository
{
    private readonly Dictionary<TagId, Tag> _byId = [];
    private readonly HashSet<(ConversationId ConversationId, TagId TagId)> _associations = [];

    public void Seed(Tag tag) => _byId[tag.Id] = tag;

    public void SeedAssociation(ConversationId conversationId, TagId tagId) =>
        _associations.Add((conversationId, tagId));

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
        _associations.RemoveWhere(a => a.TagId == tag.Id);
        return Task.CompletedTask;
    }

    public Task AddToConversationAsync(ConversationId conversationId, TagId tagId, CancellationToken cancellationToken)
    {
        _associations.Add((conversationId, tagId));
        return Task.CompletedTask;
    }

    public Task RemoveFromConversationAsync(ConversationId conversationId, TagId tagId, CancellationToken cancellationToken)
    {
        _associations.Remove((conversationId, tagId));
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<Tag>> GetForConversationAsync(ConversationId conversationId, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<Tag>>(_associations
            .Where(a => a.ConversationId == conversationId)
            .Select(a => _byId[a.TagId])
            .OrderBy(t => t.Name)
            .ToList());

    public Task<IReadOnlySet<ConversationId>> GetConversationIdsForTagAsync(
        TagId tagId, SiteId siteId, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlySet<ConversationId>>(_associations
            .Where(a => a.TagId == tagId && _byId.GetValueOrDefault(a.TagId)?.SiteId == siteId)
            .Select(a => a.ConversationId)
            .ToHashSet());
}
