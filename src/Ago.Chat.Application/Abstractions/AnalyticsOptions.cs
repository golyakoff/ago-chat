namespace Ago.Chat.Application.Abstractions;

/// <summary>
/// `23-16`: bound from `Analytics:*` config keys, validated at startup - the same
/// `.AddOptions&lt;T&gt;().Bind(...).Validate(...).ValidateOnStart()` shape every sibling options class in
/// this codebase already uses (<c>ModuleFlowReportOptions</c>, <c>BillingOptions</c>), so a typo in the
/// key fails the pod at boot rather than silently leaving every report unranked-by-rate forever.
///
/// <para><b>What this guards, stated once, at the one place a reader needs it.</b>
/// `docs/design/decisions.md` §7's amendment (2026-09-04): a rate is never refused for being built on a
/// small sample - "50% (1 of 2)" is printed, in full, next to its own fraction - but it must never be
/// used to *rank* one row above another. <see cref="MinimumSampleForRate"/> is the line: a row whose
/// rate's own denominator (<c>RecordedCount</c>, in every bucket that carries a rate) falls below it is
/// still shown, still carries its real numerator and denominator, and is never silenced - it is simply
/// not competing on its own rate for rank position, the same "the threshold ranks, it does not silence"
/// sentence the amendment states directly. See <c>ConversionReportReadStore</c>/<c>TagBreakdownReadStore</c>
/// for where that split actually happens.</para>
///
/// <para><b>One options class shared by both rate-bearing read stores, not one per store.</b> This is a
/// single business rule ("how much sample counts as enough to rank on"), not two different report-level
/// concerns that happen to share a name - the same reason `ConversionBucket`/`TagBreakdownBucket` already
/// carry the identical `RecordedCount`/`ConversionRate` shape rather than each report inventing its own.
/// <c>OperatorAnalyticsReadStore</c>/<c>ModuleFlowReadStore</c> take no dependency on this class at all:
/// neither report renders a rate, so there is no thin denominator for a threshold to guard there - see
/// each store's own remarks for why their existing order stands as a stable *listing*, not a ranking.
/// </para>
///
/// <para><b>Default is ten, unmeasured, and says so - the same "hardcode a sane unmeasured default"
/// precedent `RegisterSiteRateLimitOptions` already sets, contrasted deliberately with
/// <c>BillingOptions.PricePerSeatRub</c>'s own no-default rule.</b> A wrong guess here costs a
/// reordered table row a site owner can see and mentally correct for; it never charges a card or
/// silently drops data, which is the distinction `CLAUDE.md`'s "measure or stay silent" rule draws
/// between a figure worth shipping unmeasured and one that is not. Ten is round and small enough that a
/// genuinely active operator or tag clears it within days, not months.</para>
/// </summary>
public sealed class AnalyticsOptions
{
    public const string SectionName = "Analytics";

    public int MinimumSampleForRate { get; set; } = 10;
}
