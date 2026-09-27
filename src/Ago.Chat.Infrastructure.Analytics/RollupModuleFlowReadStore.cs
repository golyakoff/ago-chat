using Ago.Chat.Application.Abstractions;
using Ago.Chat.Domain;
using Dapper;

namespace Ago.Chat.Infrastructure.Analytics;

/// <summary>
/// `26-237`/`adr/0186` (module-flow funnel rollup): the precomputed-rollup implementation of
/// <see cref="IModuleFlowReadStore"/>, the same shape `26-223`'s <see cref="RollupOperatorAnalyticsReadStore"/>
/// and this epic's <see cref="RollupConversionReportReadStore"/>/<see cref="RollupOperatorLoadReportReadStore"/>
/// established. It answers the identical port question the live
/// <c>Ago.Chat.Infrastructure.Postgres.ModuleFlowReadStore</c> does - and returns the identical
/// <see cref="ModuleFlowReportResult"/> - but instead of a <c>count(*) filter (...)</c> join over
/// <c>module_tasks</c>/<c>conversations</c> on the operational database on every request, it reads a
/// grouped range scan over the narrow <c>analytics_module_flow_rollups</c> table in the dedicated
/// <c>ago_analytics</c> database: O(days), independent of total history, and it never touches
/// <c>module_tasks</c>, <c>conversations</c>, or <c>ago_chat</c> at all. It is the last analytics read to
/// move off the live path (this epic's directive: every report reads <c>ago_analytics</c>).
///
/// <para><b>Semantics preserved exactly.</b> The live report counts tasks whose <c>opened_at</c> falls in
/// the window (<see cref="ModuleFlowReportResult.FlowsStarted"/>) and, of those, the subset whose state is
/// <em>Closed at query time</em> regardless of when they closed (<see cref="ModuleFlowReportResult.FlowsClosed"/>).
/// The rollup reproduces this because AGO Chat stamps <em>both</em> the task's open and its close analytics
/// event with the task's open instant, so both land in the open day's slice and the ago-analytics funnel
/// fold counts started-and-later-closed per task within that slice; this read simply sums the two counters
/// across the window's days for the one configured module key. A task still open contributes only to
/// <c>flows_started_count</c>, matching the live store's "still Open counts as started only".</para>
///
/// <para><b>One module key, keyed by the caller's <see cref="ModuleKey"/>.</b> The funnel report is always
/// about one configured module (<c>ModuleFlowReportOptions</c>); the rollup is keyed by module key, so this
/// is a <c>where module_key = @ModuleKey</c> filter, never a literal in this file (the live store's own
/// remarks on guard 9 apply identically - the value is caller-supplied, from configuration).</para>
///
/// <para><b>The window maps to <c>local_day</c> by the same UTC-date bound the sibling rollup reads use</b>
/// (the unchanged port carries no tenant zone) - within `adr/0186`'s accepted staleness; see
/// <see cref="RollupOperatorAnalyticsReadStore"/>'s remarks for the full reasoning. A window that matches no
/// rollup day sums to zero (SQL <c>sum</c> over no rows is null, coalesced to 0), the honest all-zero the
/// live store's single <c>count(*)</c>-over-empty row also produces.</para>
/// </summary>
public sealed class RollupModuleFlowReadStore(AnalyticsDbDataSource dataSource) : IModuleFlowReadStore
{
    // A grouped range scan on the rollup primary key, summing the two task counts across the requested local
    // days for the one requested module key. `sum(bigint)` returns `numeric` in Postgres and null over no
    // rows; cast back to bigint and coalesce to 0 so it maps to the non-nullable long columns of
    // ModuleFlowReportResult and an empty window is an honest zero, not a null-map failure.
    private const string ModuleFlowReportSql = """
        select
            coalesce(sum(flows_started_count), 0)::bigint as "FlowsStarted",
            coalesce(sum(flows_closed_count), 0)::bigint  as "FlowsClosed"
        from analytics_module_flow_rollups
        where site_id = @SiteId
          and module_key = @ModuleKey
          and local_day >= @FromDay
          and local_day <= @ToDay
        """;

    public async Task<ModuleFlowReportResult> GetSiteModuleFlowReportAsync(
        SiteId siteId, ModuleKey moduleKey, DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken)
    {
        // Window bounds as calendar days at DateTimeKind.Unspecified so Npgsql binds them as `timestamp
        // without time zone` against the `date` column - see RollupOperatorAnalyticsReadStore for the full
        // reasoning; inclusive both ends, the identical mapping the sibling rollup reads use.
        var fromDay = DateTime.SpecifyKind(from.UtcDateTime.Date, DateTimeKind.Unspecified);
        var toDay = DateTime.SpecifyKind(to.UtcDateTime.Date, DateTimeKind.Unspecified);

        await using var connection = await dataSource.Value.OpenConnectionAsync(cancellationToken);

        return await connection.QuerySingleAsync<ModuleFlowReportResult>(new CommandDefinition(
            ModuleFlowReportSql,
            new
            {
                SiteId = siteId.Value,
                // moduleKey.Value - a runtime comparison against the caller-supplied configured value,
                // never a literal in this file (IModuleFlowReadStore's own remarks on guard 9).
                ModuleKey = moduleKey.Value,
                FromDay = fromDay,
                ToDay = toDay,
            },
            cancellationToken: cancellationToken));
    }
}
