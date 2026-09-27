using Ago.Chat.Application.Abstractions;
using Dapper;

namespace Ago.Chat.Infrastructure.Analytics;

/// <summary>
/// `26-223`/`adr/0186` §7.3, §8: the freshness marker behind every analytics response's additive
/// <c>computedAsOf</c> field. One query against <c>analytics_rollup_runs</c> in the <c>ago_analytics</c>
/// database - the last successful rollup run's completion instant. A failed run writes no metadata row and
/// leaves the previous rollup in place, so <c>max(completed_at)</c> simply does not advance: the reader sees
/// honestly older data, never silently-partial data.
/// </summary>
public sealed class AnalyticsFreshnessReadStore(AnalyticsDbDataSource dataSource) : IAnalyticsFreshnessReadStore
{
    // `max(completed_at)` over an empty table returns a single NULL row, so QuerySingle maps cleanly to a
    // null DateTimeOffset? for a pipeline that has never completed a run - no special "no rows" branch.
    private const string LastRunSql = """
        select max(completed_at) from analytics_rollup_runs
        """;

    public async Task<DateTimeOffset?> GetLastRollupCompletedAtAsync(CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.Value.OpenConnectionAsync(cancellationToken);

        // Read the timestamptz as DateTime, then label it UTC and wrap - the same "Npgsql/Dapper over raw
        // ADO hands back a DateTime, the read store converts it to a labelled DateTimeOffset" convention
        // the operational read stores use (e.g. ChannelDeliveryReadStore). max(...) over an empty table
        // yields NULL -> null here, the "no run has ever completed" case.
        var completedAt = await connection.QuerySingleAsync<DateTime?>(new CommandDefinition(
            LastRunSql, cancellationToken: cancellationToken));

        return completedAt is null
            ? null
            : new DateTimeOffset(DateTime.SpecifyKind(completedAt.Value, DateTimeKind.Utc));
    }
}
