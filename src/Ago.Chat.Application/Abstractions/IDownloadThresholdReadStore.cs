namespace Ago.Chat.Application.Abstractions;

/// <summary>
/// `25-83`: the read half of `tier_download_thresholds` - "two thresholds per tariff tier, stored in
/// the database, platform-owner configurable" (`docs/backlog/25-83-*.md`'s own decision). A Dapper
/// read store, not a method folded onto a write port: this codebase's existing split
/// (adr/0004, restated by <see cref="IAttachmentEgressReadStore"/>'s own remarks) is "writes through a
/// dedicated adapter, reads through Dapper" - and this port has no write sibling in
/// <c>Ago.Chat.Application</c> at all, because nothing in this product's own request path ever writes
/// a threshold: `docs/backlog/25-83-*.md`'s own Scope decided a runbook script edits these two numbers
/// by hand (see this item's own report for why no console screen ships with it this pass), so the only
/// writer is a person running SQL directly, never a handler this layer would need to expose a port
/// for.
///
/// <para><b>A missing row fails open, not closed.</b> <see cref="GetForTierAsync"/> never throws for a
/// tier with no row in <c>tier_download_thresholds</c> - it returns a pair of thresholds no real
/// tenant could ever reach (<see cref="DownloadThresholds.Unbounded"/>). The alternative - refusing
/// every download for a tier the platform owner simply has not configured yet - would turn a
/// configuration gap into an outage for every tenant on that tier the moment it is created, which is
/// the wrong direction to fail for the same reason `IAttachmentEgressMeter`'s own undercounting
/// egress figure is accepted as "safe" only because it errs toward *not* blocking a tenant who should
/// technically be blocked, never the reverse.</para>
/// </summary>
public interface IDownloadThresholdReadStore
{
    Task<DownloadThresholds> GetForTierAsync(string tier, CancellationToken cancellationToken);
}

/// <param name="Tier">The tariff tier these thresholds apply to - <see cref="Domain.SubscriptionTierBands"/>'s
/// own tier names (<c>"free"</c>, <c>"starter"</c>), read back exactly as stored.</param>
/// <param name="SoftThresholdBytes">Crossing this - an active notification (email plus a
/// non-dismissable console banner), never a refusal.</param>
/// <param name="HardThresholdBytes">Crossing this refuses every presigned GET, operator and visitor
/// alike, unless the site carries <see cref="Domain.Site.DownloadBlockExempt"/>.</param>
/// <param name="AutoBillCapRub">
/// `25-84`'s own second open question, answered: <b>yes, auto-bill gets a secondary ceiling, and it is
/// owner-configurable per tier rather than a constant.</b> The most a site on this tier may accrue in
/// download-overage charges within one calendar month before the `25-83` block returns despite
/// <see cref="Domain.DownloadOverageBillingMode.AutoBill"/>. <see langword="null"/> means uncapped -
/// the platform owner's own explicit choice to let it run, available but not the shipped default.
///
/// <para><b>Why a cap at all.</b> Every other charge this product makes is initiated by the tenant -
/// they pick a seat count, they buy an Administrator slot. This one is driven by *third parties*: the
/// tenant's own visitors clicking download links. An unbounded charge produced by somebody else's
/// behaviour is the shape that ends in a chargeback and a refund rather than revenue, and the tenant
/// who "did nothing" is factually right. The cost of being wrong in the other direction is also
/// asymmetric: with no cap, the ceiling on a month's damage is whatever a script can fetch; with a cap,
/// the worst case is a tenant blocked at a number the owner chose, which is a state this product
/// already handles end to end (`25-83` built exactly it) and which the owner can lift in one write.
///
/// <para><b>Per tier, not deployment-wide - unlike the price itself.</b> `docs/backlog/25-84-*.md`
/// settles the *price* as deployment-wide ("this price is deployment-wide rather than per-tier unless
/// the owner says otherwise"), and that is right: a gigabyte costs what a gigabyte costs, regardless of
/// who downloaded it. A *cap* is the opposite kind of number - it is a judgement about how much
/// exposure a particular kind of customer should be allowed, and a free-tier tenant who has never paid
/// anything and a Business tenant on a real subscription are not the same judgement. So it lives here,
/// in the per-tier table `25-83` already built for exactly this kind of per-tier download policy, and
/// the runbook that already edits that table (`docs/runbooks/download-threshold-tuning.md`) edits this
/// column too.</para></para>
/// </param>
public sealed record DownloadThresholds(string Tier, long SoftThresholdBytes, long HardThresholdBytes, decimal? AutoBillCapRub)
{
    /// <summary>See this type's own remarks: what a tier with no configured row resolves to - large
    /// enough that no real tenant's own monthly egress will ever reach it, so a missing configuration
    /// row never blocks a download, only ever fails to warn about one. <see cref="AutoBillCapRub"/> is
    /// <c>0</c>, not <see langword="null"/>: a tier nobody configured has no hard threshold to be over
    /// in the first place, so the cap is unreachable either way - and <c>0</c> is the safe reading of
    /// the two if the thresholds are ever made finite without this column being set.</summary>
    public static DownloadThresholds Unbounded(string tier) => new(tier, long.MaxValue, long.MaxValue, 0m);
}
