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
public sealed record DownloadThresholds(string Tier, long SoftThresholdBytes, long HardThresholdBytes)
{
    /// <summary>See this type's own remarks: what a tier with no configured row resolves to - large
    /// enough that no real tenant's own monthly egress will ever reach it, so a missing configuration
    /// row never blocks a download, only ever fails to warn about one.</summary>
    public static DownloadThresholds Unbounded(string tier) => new(tier, long.MaxValue, long.MaxValue);
}
