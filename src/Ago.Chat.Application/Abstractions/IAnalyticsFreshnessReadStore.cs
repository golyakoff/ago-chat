namespace Ago.Chat.Application.Abstractions;

/// <summary>
/// `26-223`/`adr/0186` (`docs/design/analytics-precompute.md` §3.1, §7.3, §8): the one piece of state the
/// analytics reads consult <em>beyond</em> the aggregate rollup itself - the freshness marker every
/// analytics response now carries. The precomputed-rollup model is explicitly eventually consistent, so a
/// reader must be told how current the numbers are ("computed on data as of …") rather than mistaking a
/// bounded lag for live data.
///
/// <para><b>A dedicated port, not a timestamp threaded through every aggregate method</b> (design §8): the
/// existing read ports (<see cref="IOperatorAnalyticsReadStore"/>, <see cref="IConversionReportReadStore"/>,
/// <see cref="ITagBreakdownReadStore"/>) keep their exact query signatures - the additive
/// <c>computedAsOf</c> field comes from this one small port the analytics endpoints call once alongside the
/// aggregate read, so an aggregate-shaped handler gains one freshness read and one response field rather
/// than a new parameter on every method.</para>
///
/// <para><b>Backed by the <c>ago_analytics</c> Postgres database, read-only via Dapper</b> - the same
/// store the rollup read stores read (design §3.2, §8), never the operational <c>ago_chat</c>. When the
/// analytics pipeline is not configured for a host, this resolves to a null-object implementation that
/// returns <see langword="null"/> (the reports then fall back to live <c>ago_chat</c> aggregation, whose
/// data has no rollup as-of marker) - see <c>ChatModule</c>'s own conditional wiring.</para>
/// </summary>
public interface IAnalyticsFreshnessReadStore
{
    /// <summary>The completion instant of the last successful rollup run
    /// (<c>SELECT max(completed_at) FROM analytics_rollup_runs</c>, UTC), or <see langword="null"/> when no
    /// run has ever completed - a fresh pipeline, or one that has only ever failed (a failed run writes no
    /// metadata row, so the marker honestly does not advance, design §7.3). Rendered in the caller's zone
    /// by the presentation layer (`date-and-time.md`).</summary>
    Task<DateTimeOffset?> GetLastRollupCompletedAtAsync(CancellationToken cancellationToken);
}
