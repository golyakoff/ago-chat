using Ago.Chat.Domain;

namespace Ago.Chat.Application.Abstractions;

/// <summary>
/// `25-84`: the read half of <c>download_overage_charges</c> - a Dapper read store, not methods folded
/// onto <see cref="IDownloadOverageChargeRepository"/>, the identical adr/0004 split
/// <see cref="IAttachmentEgressReadStore"/>/<see cref="IAttachmentEgressMeter"/> already draw for the
/// table this one joins against. Both members here answer an aggregate question (a <c>SUM</c>, an
/// <c>EXISTS</c>) that no aggregate load could answer without pulling every row of a month into
/// memory.
///
/// <para><b>Read live, never cached</b> - <see cref="GetSettlementAsync"/> is consulted by
/// <c>GetAttachmentDownloadUrlHandler</c> to decide whether to mint a presigned URL, which is exactly
/// the compare-and-set-adjacent read CLAUDE.md rule 8 forbids caching. It is reached only after the
/// hard threshold has already been crossed, so the ordinary download path pays nothing for it.</para>
/// </summary>
public interface IDownloadOverageReadStore
{
    /// <summary>Where <paramref name="siteId"/> stands on <paramref name="periodMonth"/>'s own overage:
    /// how much has already been turned into money, and whether the tenant has completed a real
    /// checkout for this month. A month with no rows at all answers zeros and
    /// <see langword="false"/> - "never charged" and "charged nothing" are the same fact to every
    /// caller here.</summary>
    Task<DownloadOverageSettlement> GetSettlementAsync(SiteId siteId, DateOnly periodMonth, CancellationToken cancellationToken);

    /// <summary>Every month up to and including <paramref name="upToPeriodMonth"/> in which
    /// <paramref name="siteId"/>'s own recorded egress exceeds <paramref name="hardThresholdBytes"/> by
    /// more than has already been settled - what a renewal has to sweep onto its own charge.
    ///
    /// <para><b><paramref name="hardThresholdBytes"/> is supplied by the caller, not looked up here</b>
    /// - the threshold belongs to the site's *current* tier, and this store has no business resolving
    /// one. It is also, deliberately, applied to historical months as well as the current one: this
    /// codebase keeps no history of what a tier's threshold was in March, so a tenant who changed tier
    /// mid-way has their unsettled backlog measured against the tier they are on now. Stated rather
    /// than hidden - building threshold history to make this exact is a real item, and not this
    /// one.</para></summary>
    Task<IReadOnlyList<DownloadOverageOutstanding>> GetOutstandingAsync(
        SiteId siteId, long hardThresholdBytes, DateOnly upToPeriodMonth, CancellationToken cancellationToken);
}

/// <param name="SettledBytes">The sum of <c>bytes_over</c> across this month's succeeded charges - how
/// far past the hard threshold this tenant has already been charged for.</param>
/// <param name="SettledAmountRub">The sum of <c>amount_rub</c> across the same rows - what the cap in
/// <see cref="DownloadThresholds.AutoBillCapRub"/> is measured against, together with whatever is still
/// outstanding right now.</param>
/// <param name="HasPaidCheckout">Whether this tenant completed a real, verified checkout for this
/// month - the <see cref="Domain.DownloadOverageBillingMode.Manual"/> path's own unblock, and nothing
/// else. An <see cref="Domain.DownloadOverageChargeSource.Invoice"/> row does not set this: money that
/// moved on the auto-bill path is not a manual tenant's own decision to keep going.</param>
public sealed record DownloadOverageSettlement(long SettledBytes, decimal SettledAmountRub, bool HasPaidCheckout)
{
    public static DownloadOverageSettlement None { get; } = new(0, 0m, false);
}

/// <param name="PeriodMonth">The calendar month the unsettled bytes belong to.</param>
/// <param name="OutstandingBytes">Bytes past the hard threshold that no succeeded charge covers yet -
/// always strictly positive; a month with nothing outstanding is simply absent from the list.</param>
/// <param name="HasPaidCheckout">Whether the tenant completed a real checkout in this month - what
/// makes a <see cref="Domain.DownloadOverageBillingMode.Manual"/> tenant liable for the remainder of
/// it. See <c>ProcessSubscriptionRenewalHandler</c>'s own remarks on why a manual tenant who never
/// opted in is never swept.</param>
public sealed record DownloadOverageOutstanding(DateOnly PeriodMonth, long OutstandingBytes, bool HasPaidCheckout);
