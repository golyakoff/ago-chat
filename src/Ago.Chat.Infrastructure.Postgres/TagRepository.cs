using Ago.Chat.Application.Abstractions;
using Ago.Chat.Domain;
using Ago.Chat.Infrastructure.Postgres.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Ago.Chat.Infrastructure.Postgres;

public sealed class TagRepository(AgoChatDbContext db) : ITagRepository
{
    public Task<Tag?> GetByIdAsync(TagId id, SiteId siteId, CancellationToken cancellationToken) =>
        db.Tags.FirstOrDefaultAsync(t => t.Id == id && t.SiteId == siteId, cancellationToken);

    // ITagRepository.GetByNameAsync's own remarks: an in-memory OrdinalIgnoreCase scan over this
    // site's own small, bounded tag list - the database's unique index is case-sensitive, this is the
    // best-effort duplicate guard on top of it.
    public async Task<Tag?> GetByNameAsync(SiteId siteId, string name, CancellationToken cancellationToken)
    {
        var all = await GetAllForSiteAsync(siteId, cancellationToken);
        return all.FirstOrDefault(t => string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase));
    }

    public async Task<IReadOnlyList<Tag>> GetAllForSiteAsync(SiteId siteId, CancellationToken cancellationToken) =>
        await db.Tags.Where(t => t.SiteId == siteId).OrderBy(t => t.Name).ToListAsync(cancellationToken);

    public async Task SaveAsync(Tag tag, CancellationToken cancellationToken)
    {
        if (db.Entry(tag).State == EntityState.Detached)
        {
            db.Tags.Add(tag);
        }

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            // TagNameConflictException's own remarks: the database's own enforcement of the same
            // duplicate-name invariant CreateTagHandler/RenameTagHandler already check optimistically.
            throw new TagNameConflictException($"A tag named '{tag.Name}' already exists for this site.");
        }
    }

    public async Task DeleteAsync(Tag tag, CancellationToken cancellationToken)
    {
        db.Tags.Remove(tag);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task AddToConversationAsync(ConversationId conversationId, TagId tagId, CancellationToken cancellationToken)
    {
        // Raw SQL, not `db.ConversationTags.Add(...)` + SaveChangesAsync - AddToConversationAsync's
        // own contract is idempotent (ITagRepository's remarks), and EF's Add would throw on the
        // composite-PK conflict a second call produces. `ON CONFLICT DO NOTHING` is the direct,
        // one-statement expression of "idempotent" rather than a catch-and-ignore around a thrown
        // DbUpdateException.
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"""
            insert into conversation_tags (conversation_id, tag_id)
            values ({conversationId.Value}, {tagId.Value})
            on conflict (conversation_id, tag_id) do nothing
            """,
            cancellationToken);
    }

    public async Task RemoveFromConversationAsync(
        ConversationId conversationId, TagId tagId, CancellationToken cancellationToken) =>
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"delete from conversation_tags where conversation_id = {conversationId.Value} and tag_id = {tagId.Value}",
            cancellationToken);

    public async Task<IReadOnlyList<Tag>> GetForConversationAsync(
        ConversationId conversationId, CancellationToken cancellationToken) =>
        await db.ConversationTags
            .Where(x => x.ConversationId == conversationId)
            .Join(db.Tags, x => x.TagId, t => t.Id, (x, t) => t)
            .OrderBy(t => t.Name)
            .ToListAsync(cancellationToken);

    public async Task<IReadOnlySet<ConversationId>> GetConversationIdsForTagAsync(
        TagId tagId, SiteId siteId, CancellationToken cancellationToken)
    {
        // The join through `tags` re-checks the site even though every caller has already resolved
        // `tagId` via GetByIdAsync(id, siteId, ...) first - a cheap defence-in-depth check over a
        // small, bounded table, the same "checked twice" shape ConversationReadStore.GetByIdAsync's
        // own site_id predicate keeps even where a caller is already trusted.
        var ids = await db.ConversationTags
            .Where(x => x.TagId == tagId)
            .Join(db.Tags.Where(t => t.SiteId == siteId), x => x.TagId, t => t.Id, (x, _) => x.ConversationId)
            .ToListAsync(cancellationToken);

        return ids.ToHashSet();
    }
}
