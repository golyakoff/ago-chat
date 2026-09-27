namespace Ago.Chat.Infrastructure.Analytics;

/// <summary>
/// `26-223`/`adr/0186` (`docs/design/analytics-precompute.md` §7): the <c>dimension_type</c> vocabulary
/// stored in <c>analytics_daily_rollups.dimension_type</c>. A conversation contributes to five dimension
/// rows for its local day (<see cref="Total"/> + <see cref="Channel"/> + <see cref="Operator"/> +
/// <see cref="Referrer"/> + <see cref="Campaign"/>) plus one <see cref="Tag"/> row per tag - reproducing
/// the old single-query <c>GROUPING SETS</c> output as a narrow long table.
///
/// <para><b>These strings are a cross-repository contract, not a local choice.</b> The values are written
/// by the standalone <c>ago-analytics</c> aggregator (`adr/0186`, a separate repository), so this codebase
/// cannot compile-time-check them against the writer. They are pinned here as named constants - matching
/// the design doc's own §7 vocabulary exactly - so a future reader sees the contract in one place and a
/// query never carries a bare string literal. If the aggregator's vocabulary ever changes, this file and
/// that repository change together.</para>
/// </summary>
internal static class RollupDimensionTypes
{
    /// <summary>The site-wide total for the day (<c>dimension_key</c> is the empty string).</summary>
    public const string Total = "total";

    /// <summary>One row per resolved channel label (<c>dimension_key</c> = the channel label, e.g.
    /// <c>"Widget"</c>/<c>"Sms"</c> - a denormalized value carried on the event, never a joined name).</summary>
    public const string Channel = "channel";

    /// <summary>One row per attributed operator (<c>dimension_key</c> = the operator id as text). The
    /// display name is <b>not</b> stored here (design §8.1: no cross-database join) - it is resolved in the
    /// application layer.</summary>
    public const string Operator = "operator";

    /// <summary>One row per referrer host (<c>dimension_key</c> = the host, or <c>"Direct"</c>).</summary>
    public const string Referrer = "referrer";

    /// <summary>One row per UTM campaign (<c>dimension_key</c> = the campaign value).</summary>
    public const string Campaign = "campaign";

    /// <summary>`26-223` reads this store for site/own analytics only; the tag dimension is consumed by the
    /// separate tag-breakdown read switch (design S7c). Named here for completeness of the vocabulary and
    /// so a stray <c>tag</c> row never silently lands in another dimension's list.</summary>
    public const string Tag = "tag";
}
