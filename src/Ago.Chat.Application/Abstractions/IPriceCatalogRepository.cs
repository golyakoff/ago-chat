using Ago.Chat.Domain;

namespace Ago.Chat.Application.Abstractions;

/// <summary>
/// `25-43`: the port for <see cref="PricedResource"/>/<see cref="PublishedPriceVersion"/> - declared
/// here, implemented in `Ago.Chat.Infrastructure.Postgres` (clean-architecture.md's dependency rule).
/// The identical "aggregate load/save for the one write path, direct queries for the hot read paths"
/// split <see cref="IDocumentRepository"/> already establishes for `adr/0114`'s own mechanism - this
/// item deliberately copies that shape rather than inventing a second one for what is, structurally,
/// the identical grandfathering problem (a version, an effective instant, never edited in place).
/// </summary>
public interface IPriceCatalogRepository
{
    /// <summary>The write path's own load - the whole aggregate, oldest-version-first, for
    /// <c>PublishPriceVersionHandler</c> to call <see cref="PricedResource.Publish"/> against its own
    /// <see cref="PricedResource.LastSequence"/>. <see langword="null"/> when nothing has ever been
    /// published or even attempted under this key.</summary>
    Task<PricedResource?> GetByKeyAsync(PriceKey key, CancellationToken cancellationToken);

    /// <summary>
    /// Persists <paramref name="resource"/> - a brand-new <see cref="PricedResource"/> (never saved
    /// before) is inserted together with every version in <see cref="PricedResource.Versions"/>; one
    /// already loaded through <see cref="GetByKeyAsync"/> has only its newly appended version inserted,
    /// guarded by the row's own optimistic-concurrency token. Throws
    /// <see cref="PriceCatalogConcurrencyConflictException"/> - never
    /// <c>Microsoft.EntityFrameworkCore.DbUpdateConcurrencyException</c> - if another publish for the
    /// same key committed first; see that type's own remarks for why the translation happens at this
    /// port boundary.
    /// </summary>
    Task SaveAsync(PricedResource resource, CancellationToken cancellationToken);

    /// <summary>
    /// The currently-effective price for <paramref name="key"/> - the hot read every real charge site
    /// calls at the moment it charges (`25-43`'s own Scope: "reads the currently-effective version, by
    /// key, at the moment it charges"). A direct query against <c>published_price_versions</c>,
    /// bypassing the aggregate entirely - the identical "the public read path never pays for the whole
    /// write-shaped aggregate" trade <see cref="IDocumentRepository.FindCurrentAsync"/>'s own remarks
    /// describe. <see langword="null"/> means exactly one thing: nothing has ever been published under
    /// this key - `25-43`'s own second decision states plainly that this is the ordinary "built, not
    /// yet for sale" state, and every caller of this method must treat it as a real, refuse-to-charge
    /// outcome, never a crash and never a silent zero.
    /// </summary>
    Task<PublishedPriceVersion?> FindCurrentAsync(PriceKey key, CancellationToken cancellationToken);

    /// <summary>
    /// A specific, already-published version by its own sequence number - <see langword="null"/> if no
    /// version with that exact number was ever published under that key. Immutable once it exists, so
    /// a caller may cache a hit far more aggressively than a <see cref="FindCurrentAsync"/> hit.
    /// <see cref="Domain.BillingSubscription.BaseSeatPriceVersion"/>/<see cref="Domain.BillingSubscription.ExtraSeatPriceVersion"/>
    /// are exactly the sequence numbers this method resolves - a proration that must compare "what was
    /// already being charged" against "what the new count would cost" reads the old price through this
    /// method, never through <see cref="FindCurrentAsync"/>, which would silently substitute whatever
    /// is current *now* for a historical fact.
    /// </summary>
    Task<PublishedPriceVersion?> FindVersionAsync(PriceKey key, int sequence, CancellationToken cancellationToken);
}
