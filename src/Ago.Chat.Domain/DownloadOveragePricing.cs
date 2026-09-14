namespace Ago.Chat.Domain;

/// <summary>
/// `25-84`: the metered download-overage charge, as a pure function of "how many bytes over the hard
/// threshold" and "what a gigabyte costs right now". Its own Domain class rather than three more
/// members on <see cref="SubscriptionTierBands"/> - that type is the *seat ladder* (bands, minimums,
/// the base/marginal split), and a per-gigabyte egress meter shares none of its shape; the only thing
/// the two have in common is that both end in Roubles. The alternative (folding this into
/// <see cref="SubscriptionTierBands"/>) would have made "how much does an extra seat cost" and "how
/// much did this tenant's visitors download" read as one subject.
///
/// <para><b>The price itself is not here.</b> <see cref="OveragePerGigabyteKey"/> is a
/// <see cref="PriceKey"/>, resolved through <c>IPriceCatalogRepository</c> at the moment of every
/// charge - `docs/backlog/25-84-*.md`'s own Done-when ("configurable by the platform owner, not
/// hardcoded, with 100 ₽ as the shipped default"), satisfied by the identical mechanism `25-43` built
/// for the seat prices rather than a second one. The shipped 100 ₽ is a seeded `v1` row in
/// <c>Stage25AddDownloadOverageBilling</c>, not a constant anywhere in this assembly - exactly the
/// distinction <see cref="SubscriptionTierBands.BaseSeatPriceKey"/>'s own remarks already draw.</para>
///
/// <para><b>A gigabyte here is 1024³ bytes, not 10⁹.</b> Every byte figure this charge is measured
/// against is binary - `tier_download_thresholds`' own seeded MiB/GiB numbers
/// (<c>Stage25AddTierDownloadThresholds</c>), <c>AttachmentStorageQuotaOptions</c>' own ceilings, and
/// the `bytes_out` counter itself. Mixing a decimal gigabyte into a binary threshold would make "how
/// far over am I" and "what does that cost" disagree by 7% for no reason a tenant could ever discover.
/// Stated rather than assumed because the storage provider prices in decimal gigabytes - which is the
/// second reason this figure is ours and not theirs (see `docs/backlog/25-84-*.md`'s own Outcome on
/// the raw-proxy decision).</para>
///
/// <para><b>Fractional, rounded to the kopeck - never a "started gigabyte".</b> The first byte past
/// the threshold costs a fraction of a kopeck, not 100 ₽. Rounding a partial gigabyte up to a whole
/// one would turn a tenant who went 1 MB over into a 100 ₽ invoice line, which is the surprise this
/// whole item exists to avoid. <see cref="System.MidpointRounding.AwayFromZero"/> at two decimals is
/// the identical rounding <c>PurchaseAdministratorSlotHandler</c>'s own proration already uses.</para>
/// </summary>
public static class DownloadOveragePricing
{
    /// <summary>1024³ - see this type's own remarks for why binary and not decimal.</summary>
    public const long BytesPerGigabyte = 1024L * 1024L * 1024L;

    /// <summary>`25-84`'s own priced resource, registered in <see cref="PricedResourceKeys"/> the same
    /// change that wires the charge sites below to read it - never the other way round (that type's
    /// own remarks).</summary>
    public static readonly PriceKey OveragePerGigabyteKey = new("download-overage-per-gb");

    /// <summary>What <paramref name="bytesOverThreshold"/> bytes past the hard threshold cost at
    /// <paramref name="pricePerGigabyteRub"/> per gigabyte. Zero (never negative) for a
    /// <paramref name="bytesOverThreshold"/> at or below zero - a caller that is not actually over the
    /// threshold has nothing to pay, and a negative charge is not a refund this product has any
    /// mechanism for.</summary>
    public static decimal ComputeOverageRub(long bytesOverThreshold, decimal pricePerGigabyteRub)
    {
        if (bytesOverThreshold <= 0)
        {
            return 0m;
        }

        var gigabytes = (decimal)bytesOverThreshold / BytesPerGigabyte;
        return Math.Round(gigabytes * pricePerGigabyteRub, 2, MidpointRounding.AwayFromZero);
    }
}
