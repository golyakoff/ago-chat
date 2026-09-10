using Ago.Chat.Application.Abstractions;
using Ago.Chat.Domain;
using Ago.Chat.Infrastructure.Postgres.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Ago.Chat.Infrastructure.Postgres;

/// <summary>
/// `25-43`. <see cref="GetByKeyAsync"/>/<see cref="SaveAsync"/> are the write path's own aggregate
/// load/save pair, the identical shape <c>DocumentRepository</c> already establishes for
/// <c>Document</c>/<c>PublishedDocumentVersion</c> - <see cref="SaveAsync"/>'s own concurrency
/// translation is a direct copy of that class's own two <c>catch</c> blocks, restated for
/// <see cref="PricedResource"/>. <see cref="FindCurrentAsync"/>/<see cref="FindVersionAsync"/> are the
/// hot read path's own direct queries against <c>published_price_versions</c>, bypassing the aggregate
/// entirely - <see cref="IPriceCatalogRepository"/>'s own remarks explain why both shapes live on the
/// one port rather than being split into two.
/// </summary>
public sealed class PriceCatalogRepository(AgoChatDbContext db) : IPriceCatalogRepository
{
    public Task<PricedResource?> GetByKeyAsync(PriceKey key, CancellationToken cancellationToken) =>
        db.PricedResources
            .Include("_versions")
            .FirstOrDefaultAsync(r => r.Key == key, cancellationToken);

    public async Task SaveAsync(PricedResource resource, CancellationToken cancellationToken)
    {
        // A freshly PricedResource.Create()'d resource was never loaded through this context, so it is
        // not tracked yet - the same "detached means new" test DocumentRepository.SaveAsync's own
        // remarks give for Document.
        if (db.Entry(resource).State == EntityState.Detached)
        {
            db.PricedResources.Add(resource);
        }

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            // `25-43`: the identical translation DocumentRepository.SaveAsync's own remarks explain in
            // full - clear the tracker so a caller's retry (PublishPriceVersionHandler) actually
            // re-reads Postgres's current row instead of the identity map's stale, never-committed copy.
            db.ChangeTracker.Clear();
            throw new PriceCatalogConcurrencyConflictException(resource.Id);
        }
        // `25-43`: the identical race DocumentRepository.SaveAsync's own second catch clause guards
        // against, restated for this aggregate - EF's change tracker executes Added entities before
        // Modified ones, so two publishes racing over the same key can both compute the same next
        // Sequence and the loser's new PublishedPriceVersion child row (an Added entity) hits
        // ix_published_price_versions_key_sequence before the parent PricedResource's own stale-xmin
        // UPDATE is ever attempted.
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException
        {
            SqlState: PostgresErrorCodes.UniqueViolation,
            ConstraintName: "ix_published_price_versions_key_sequence",
        })
        {
            db.ChangeTracker.Clear();
            throw new PriceCatalogConcurrencyConflictException(resource.Id);
        }
    }

    public Task<PublishedPriceVersion?> FindCurrentAsync(PriceKey key, CancellationToken cancellationToken) =>
        db.PublishedPriceVersions
            .Where(v => v.Key == key)
            .OrderByDescending(v => v.Sequence)
            .FirstOrDefaultAsync(cancellationToken);

    public Task<PublishedPriceVersion?> FindVersionAsync(PriceKey key, int sequence, CancellationToken cancellationToken) =>
        db.PublishedPriceVersions
            .FirstOrDefaultAsync(v => v.Key == key && v.Sequence == sequence, cancellationToken);
}
