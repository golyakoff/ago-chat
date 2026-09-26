using Ago.Chat.Application.Abstractions;
using Ago.Chat.Application.Mapping;
using Ago.Chat.Domain;
using Ago.Chat.Infrastructure.Postgres.Persistence;
using Ago.Platform.Abstractions;
using Ago.Platform.Kernel;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Ago.Chat.Infrastructure.Postgres;

/// <summary>`adr/0186` S1: also the <see cref="ConversationTagged"/>/<see cref="ConversationUntagged"/>
/// analytics events' one publisher - <see cref="outbox"/>/<see cref="idGenerator"/> join this adapter's
/// dependencies for the identical reason <c>ModuleQuantityGrantStore</c>'s own remarks give: a write with
/// no aggregate to raise a domain event through still owes rule 4 the same "state change and its event
/// commit together" guarantee, so the adapter that issues the raw SQL is the one place that guarantee can
/// live. See <see cref="AddToConversationAsync"/>'s own remarks for why that needs an explicit
/// transaction here specifically, unlike <c>ModuleQuantityGrantStore</c>'s own EF-tracked writes.</summary>
public sealed class TagRepository(AgoChatDbContext db, IOutboxWriter outbox, IIdGenerator idGenerator) : ITagRepository
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

    /// <summary>Raw SQL, not `db.ConversationTags.Add(...)` + SaveChangesAsync - the contract is
    /// idempotent (<see cref="ITagRepository.AddToConversationAsync"/>'s own remarks), and EF's Add
    /// would throw on the composite-PK conflict a second call produces. `ON CONFLICT DO NOTHING` is the
    /// direct, one-statement expression of "idempotent" rather than a catch-and-ignore around a thrown
    /// DbUpdateException. `19-02`: ON CONFLICT DO NOTHING also means a stale AI candidate that lost
    /// a race against an operator's own concurrent TagConversation call never overwrites the
    /// operator's row with Source='Ai' - the first writer's source wins, which is the correct
    /// "operator's own judgment is never silently relabelled" outcome either way round.
    ///
    /// <para><b>`adr/0186` S1: wrapped in an explicit transaction, unlike every EF-tracked write in
    /// this file.</b> `ExecuteSqlInterpolatedAsync` commits on its own the instant it returns - there is
    /// no pending `SaveChangesAsync` for the outbox row staged below to ride along with the way
    /// `ModuleQuantityGrantStore`'s own EF-tracked writes let it (that store's own remarks). Without an
    /// ambient transaction spanning both statements, a crash between the insert and the outbox flush
    /// would commit the tag but silently lose the fact it happened - exactly what rule 4 exists to rule
    /// out. <see cref="Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction"/> is what makes the
    /// raw SQL and the tracked outbox insert one atomic unit again.</para>
    ///
    /// <para><b>Published only when a row was actually inserted.</b> The returned affected-row count is
    /// the honest signal for "did this call actually change anything" - re-tagging an already-tagged
    /// conversation is the documented no-op case, and publishing <see cref="ConversationTagged"/> for it
    /// would fabricate a second "tagged" fact the analytics rollup would double-count
    /// (<see cref="ConversationTagged"/>'s own remarks).</para></summary>
    public async Task AddToConversationAsync(
        ConversationId conversationId, SiteId siteId, TagId tagId, TagSource source, DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        var rowsAffected = await db.Database.ExecuteSqlInterpolatedAsync(
            $"""
            insert into conversation_tags (conversation_id, tag_id, source)
            values ({conversationId.Value}, {tagId.Value}, {source.ToString()})
            on conflict (conversation_id, tag_id) do nothing
            """,
            cancellationToken);

        if (rowsAffected > 0)
        {
            outbox.Enqueue(ConversationTaggedMapper.ToEnvelope(
                conversationId.Value, siteId.Value, tagId.Value, now, idGenerator));
            await db.SaveChangesAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    /// <summary>The mirror of <see cref="AddToConversationAsync"/> - the identical explicit-transaction,
    /// publish-only-on-a-real-change shape, for <see cref="ConversationUntagged"/>.</summary>
    public async Task RemoveFromConversationAsync(
        ConversationId conversationId, SiteId siteId, TagId tagId, DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        var rowsAffected = await db.Database.ExecuteSqlInterpolatedAsync(
            $"delete from conversation_tags where conversation_id = {conversationId.Value} and tag_id = {tagId.Value}",
            cancellationToken);

        if (rowsAffected > 0)
        {
            outbox.Enqueue(ConversationUntaggedMapper.ToEnvelope(
                conversationId.Value, siteId.Value, tagId.Value, now, idGenerator));
            await db.SaveChangesAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<ConversationTagEntry>> GetForConversationAsync(
        ConversationId conversationId, CancellationToken cancellationToken) =>
        await db.ConversationTags
            .Where(x => x.ConversationId == conversationId)
            .Join(db.Tags, x => x.TagId, t => t.Id, (x, t) => new { Tag = t, x.Source })
            .OrderBy(x => x.Tag.Name)
            .Select(x => new ConversationTagEntry(x.Tag, x.Source))
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
