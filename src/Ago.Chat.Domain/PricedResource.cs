namespace Ago.Chat.Domain;

/// <summary>
/// `25-43`: one row per <see cref="PriceKey"/> - the aggregate root that owns the ordering of every
/// <see cref="PublishedPriceVersion"/> published under that key, the identical role <see cref="Document"/>
/// plays for its own <see cref="PublishedDocumentVersion"/>s (`adr/0114`'s own shape, which this item's
/// own text cites by name as the mechanism to copy: "a version, an effective instant, never edited in
/// place - a correction is a new version"). Deliberately mirrors that type closely rather than
/// inventing a parallel shape: the design problem is identical (grandfathering - a later correction
/// must never be observed to have changed what an earlier reader already saw), only the payload
/// differs (a Rouble amount instead of a title and a body).
///
/// <para><b>Why a second table, restated for a price instead of a document.</b> The single biggest
/// risk this item's own text names is letting "the current price" be one row a write overwrites in
/// place - a report, a stale cache, or a historical charge's own display reading that row at the wrong
/// instant would silently reprice the past. Keeping <see cref="LastSequence"/> and the concurrency
/// token on <em>this</em> row, with every actual price living on an insert-only child, is what makes
/// that mistake structurally unavailable: there is no method anywhere on this type that mutates a
/// <see cref="PublishedPriceVersion"/> already in <see cref="Versions"/>, only <see cref="Publish"/>,
/// which appends.</para>
///
/// <para><b>Concurrency: the identical `xmin`/retry shape `24-02` established for <see cref="Document"/>.</b>
/// Two concurrent publishes for the same key must not both compute <c>LastSequence + 1</c> from the
/// same stale read and collide - <see cref="IPriceCatalogRepository"/>'s own remarks (Application)
/// describe the translated exception a handler catches and retries against a freshly reloaded
/// aggregate. In practice this row is written by exactly one caller (the platform owner,
/// `25-43`'s own Scope), so contention is not a load concern here any more than it was for
/// <see cref="Document"/> - the mechanism is reused because it already exists and is already proven.</para>
///
/// <para><b>Never deleted, and nothing in this codebase deletes a <see cref="PublishedPriceVersion"/>
/// either.</b> A superseded price stays readable forever - the same structural guarantee
/// <see cref="Document"/>'s own remarks state, for the identical reason: a historical charge that
/// names a version must always be able to answer "what did that version actually charge".</para>
/// </summary>
public sealed class PricedResource
{
    public PricedResourceId Id { get; }

    public PriceKey Key { get; }

    /// <summary>The last <see cref="PublishedPriceVersion.Sequence"/> handed out for this key - the
    /// counter <see cref="Publish"/> increments before minting the next version, the identical role
    /// <see cref="Document.LastSequence"/> plays.</summary>
    public int LastSequence { get; private set; }

    private readonly List<PublishedPriceVersion> _versions = [];

    /// <summary>Every version ever published under this key, oldest first - small and bounded (a
    /// price changes a handful of times over the life of this product, not thousands), the identical
    /// "plain unbounded list" shape <see cref="Document.Versions"/>'s own remarks already accept.</summary>
    public IReadOnlyList<PublishedPriceVersion> Versions => _versions;

    /// <summary>The most recently published version - the one every real charge site reads at the
    /// moment it charges. <see langword="null"/> for a key with no version published yet, which
    /// `25-43`'s own second decision states plainly is the ordinary state for "built, not yet for
    /// sale", never an error condition a caller must work around.</summary>
    public PublishedPriceVersion? Current => _versions.Count == 0 ? null : _versions[^1];

    private PricedResource(PricedResourceId id, PriceKey key)
    {
        Id = id;
        Key = key;
    }

    // EF Core materialization only (1-04's precedent) - never called by domain code.
    private PricedResource()
    {
    }

    /// <summary>A brand-new priced resource, no version published yet. Unlike <see cref="Document.Create"/>,
    /// this factory does not itself validate that <paramref name="key"/> is one code has registered -
    /// <see cref="PriceKey"/>'s own remarks explain why that check lives one layer up
    /// (<c>PublishPriceVersionHandler</c>), not here: a <see cref="PriceKey"/> reaching this
    /// constructor is already known-legal shape, and "is it a registered key" is a catalogue question,
    /// not an invariant of one row.</summary>
    public static PricedResource Create(PricedResourceId id, PriceKey key) => new(id, key);

    /// <summary>Mints and appends the next <see cref="PublishedPriceVersion"/> - the only way one is
    /// ever created. Increments <see cref="LastSequence"/> first, so the new version's own
    /// <see cref="PublishedPriceVersion.Sequence"/> is always strictly greater than every version
    /// already in <see cref="Versions"/>, the identical ordering guarantee <see cref="Document.Publish"/>
    /// already gives.</summary>
    public PublishedPriceVersion Publish(PublishedPriceVersionId versionId, decimal amountRub, DateTimeOffset publishedAt)
    {
        LastSequence++;
        var version = PublishedPriceVersion.Create(versionId, Id, Key, LastSequence, amountRub, publishedAt);
        _versions.Add(version);
        return version;
    }
}
