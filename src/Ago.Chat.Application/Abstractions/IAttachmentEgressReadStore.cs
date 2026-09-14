using Ago.Chat.Domain;

namespace Ago.Chat.Application.Abstractions;

/// <summary>`23-82`'s own Done-when: "the measured figure is written down... visible to us." The read
/// half of <see cref="IAttachmentEgressMeter"/>'s aggregate - a Dapper read store (adr/0004), not a
/// method folded onto the write port, matching this codebase's existing split between a write port
/// used inside a transaction (<see cref="ISiteAttachmentStorageBudget"/>) and a read store used from a
/// query handler (<see cref="IAttachmentBudgetReadStore"/>, this item's own sibling for the same
/// reason).</summary>
public interface IAttachmentEgressReadStore
{
    /// <summary>Zero download count and zero bytes, never a missing row, when <paramref name="siteId"/>
    /// has recorded no downloads for <paramref name="periodMonth"/> - "no evidence" and "measured
    /// zero" are the same fact for a tenant nobody has downloaded from yet, and a caller should not
    /// have to tell a missing row apart from a real zero.</summary>
    Task<SiteAttachmentEgress> GetForSiteAsync(SiteId siteId, DateOnly periodMonth, CancellationToken cancellationToken);
}

public sealed record SiteAttachmentEgress(SiteId SiteId, DateOnly PeriodMonth, long DownloadCount, long BytesOut);
