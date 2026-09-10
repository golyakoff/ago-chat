namespace Ago.Chat.Domain;

/// <summary>
/// `25-43`: one published, immutable price for one <see cref="PriceKey"/> - the thing a real charge
/// site actually reads, and the thing a completed charge (`Domain.BillingSubscription`'s own
/// <c>BaseSeatPriceVersion</c>/<c>ExtraSeatPriceVersion</c>) names. Constructed only through
/// <see cref="PricedResource.Publish"/>, never directly - the identical "the aggregate root is the one
/// place a child is created" shape <see cref="Document.Publish"/> already establishes for
/// <see cref="PublishedDocumentVersion"/>. Insert-only, no method anywhere on this type that changes
/// <see cref="AmountRub"/> once set - a version is evidence of what a charge actually used, and
/// overwriting it in place would silently reprice whatever already pointed at it.
///
/// <para><b><see cref="Version"/> is server-assigned from <see cref="Sequence"/>, never a caller's own
/// string</b> - the identical reasoning <see cref="PublishedDocumentVersion"/>'s own remarks give in
/// full: stable, ordered and human-quotable simultaneously, none of which a caller-supplied label
/// could be trusted to hold together.</para>
///
/// <para><b><see cref="Key"/> is denormalised onto this row, not reached through
/// <see cref="PricedResourceId"/> alone</b> - the identical trade <see cref="PublishedDocumentVersion.DocumentKey"/>'s
/// own remarks describe: every real charge site's own hot read (`IPriceCatalogRepository.FindCurrentAsync`)
/// filters by key directly, and a version can never drift to a different <see cref="PricedResource"/>
/// once published, so the duplication can never go stale.</para>
/// </summary>
public sealed class PublishedPriceVersion
{
    public PublishedPriceVersionId Id { get; }

    public PricedResourceId PricedResourceId { get; }

    public PriceKey Key { get; }

    /// <summary>Assigned by <see cref="PricedResource.Publish"/> from <see cref="PricedResource.LastSequence"/> -
    /// never supplied by a caller. The true ordering key; <see cref="Version"/> is this value's own
    /// human-facing spelling.</summary>
    public int Sequence { get; }

    /// <summary><c>"v{Sequence}"</c> - see this type's own remarks for why deriving it from
    /// <see cref="Sequence"/> is what makes it stable, ordered and human-quotable at once.</summary>
    public string Version { get; } = string.Empty;

    /// <summary>The Rouble figure this version charges - never negative (a price of zero is legal and
    /// meaningful, e.g. a promotional rate; a negative one is not a price at all). This is the one
    /// field `ago-business`/the platform owner ever actually decides; everything else on this type is
    /// this mechanism's own bookkeeping.</summary>
    public decimal AmountRub { get; }

    public DateTimeOffset PublishedAt { get; }

    private PublishedPriceVersion(
        PublishedPriceVersionId id, PricedResourceId pricedResourceId, PriceKey key, int sequence, string version,
        decimal amountRub, DateTimeOffset publishedAt)
    {
        Id = id;
        PricedResourceId = pricedResourceId;
        Key = key;
        Sequence = sequence;
        Version = version;
        AmountRub = amountRub;
        PublishedAt = publishedAt;
    }

    // EF Core materialization only (1-04's precedent) - never called by domain code.
    private PublishedPriceVersion()
    {
    }

    /// <summary>Internal: only <see cref="PricedResource.Publish"/> may construct one - the identical
    /// "aggregate root is the sole factory for its own child" shape
    /// <see cref="PublishedDocumentVersion.Create"/> already establishes. <paramref name="sequence"/>
    /// is trusted as already-validated (positive, already incremented) by the caller.</summary>
    internal static PublishedPriceVersion Create(
        PublishedPriceVersionId id, PricedResourceId pricedResourceId, PriceKey key, int sequence, decimal amountRub,
        DateTimeOffset publishedAt)
    {
        if (amountRub < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(amountRub), amountRub, "A published price cannot be negative.");
        }

        return new PublishedPriceVersion(id, pricedResourceId, key, sequence, $"v{sequence}", amountRub, publishedAt);
    }
}
