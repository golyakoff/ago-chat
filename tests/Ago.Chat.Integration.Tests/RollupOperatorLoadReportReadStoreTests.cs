using Ago.Chat.Application.Abstractions;
using Ago.Chat.Domain;
using Ago.Chat.Infrastructure.Analytics;
using Dapper;
using Npgsql;

namespace Ago.Chat.Integration.Tests;

/// <summary>
/// `26-237`/`adr/0186` (operator-load rollup, decision B): the operator-load read switch, against a real
/// Postgres standing in for the <c>ago_analytics</c> rollup database. Proves the precomputed-rollup
/// implementation of <see cref="IOperatorLoadReportReadStore"/> sums the per-(operator, exact concurrent-load)
/// rows across the window, folds the exact loads into <see cref="AnalyticsOptions.LoadBucketUpperBounds"/>'s
/// configured buckets (the same fold the live store applies), reconstitutes reply latency from the stored
/// sum and count, and derives Standard/Additional from the summed additional-interval count - returning the
/// identical <see cref="OperatorLoadSummary"/> shape the live store does, with the operator name left null
/// for the application-layer merge (design §8.1).
///
/// <para>ago-chat owns no migration for the <c>ago_analytics</c> schema (the standalone ago-analytics
/// service does, adr/0186), so this test creates the operator-load rollup table itself from the design's own
/// shape and reads it through the same Dapper adapter production uses - the read contract, not a migration.</para>
/// </summary>
[Collection(PostgresCollection.Name)]
public class RollupOperatorLoadReportReadStoreTests(PostgresFixture fixture)
{
    private static readonly DateOnly Day1 = new(2026, 6, 10);
    private static readonly DateOnly Day2 = new(2026, 6, 11);

    // A window whose UTC-date bounds cover both rollup days (inclusive), the mapping the read store applies.
    private static readonly DateTimeOffset From = new(2026, 6, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset To = new(2026, 6, 30, 12, 0, 0, TimeSpan.Zero);

    // Default bounds [1, 3, 5, 8]: load 1 -> "1", loads 2-3 -> "2-3", load 6 -> "6-8".
    private static readonly AnalyticsOptions Options = new();

    private RollupOperatorLoadReportReadStore Store => new(new AnalyticsDbDataSource(fixture.DataSource), Options);

    [Fact]
    public async Task GetOperatorLoadReportAsync_SumsAcrossDaysAndFoldsExactLoadsIntoConfiguredBuckets()
    {
        await EnsureOperatorLoadTableAsync();
        var siteId = new SiteId(Guid.NewGuid());
        var op = Guid.NewGuid();

        await using var connection = await fixture.DataSource.OpenConnectionAsync(CancellationToken.None);

        // Load 1 (bucket "1"): 2 intervals, both replied, 30 + 30 = 60s.
        await InsertAsync(connection, siteId, Day1, op, load: 1, intervals: 2, additional: 0, distinct: 2, replies: 2, replySecs: 60);
        // Load 3 (bucket "2-3") split across two days: Day1 one replied (40s), Day2 one un-replied.
        await InsertAsync(connection, siteId, Day1, op, load: 3, intervals: 1, additional: 0, distinct: 1, replies: 1, replySecs: 40);
        await InsertAsync(connection, siteId, Day2, op, load: 3, intervals: 1, additional: 0, distinct: 1, replies: 0, replySecs: 0);
        // Load 6 (bucket "6-8"): 1 interval, additional (over capacity at close), replied 90s.
        await InsertAsync(connection, siteId, Day1, op, load: 6, intervals: 1, additional: 1, distinct: 1, replies: 1, replySecs: 90);

        var result = await Store.GetOperatorLoadReportAsync(siteId, From, To, CancellationToken.None);

        var summary = Assert.Single(result);
        Assert.Equal(op, summary.Operator.Value);
        // The operator display name is not in the rollups (design §8.1) - the handler resolves it.
        Assert.Null(summary.OperatorName);
        Assert.Equal(5, summary.IntervalsHeld);          // 2 + 1 + 1 + 1
        Assert.Equal(5, summary.ConversationsHeld);       // 2 + 1 + 1 + 1
        Assert.Equal(1, summary.AdditionalIntervals);     // only the load-6 interval
        Assert.Equal(4, summary.StandardIntervals);       // 5 - 1

        Assert.Equal(["1", "2-3", "6-8"], summary.ByLoad.Select(b => b.BucketLabel).ToArray());

        var bucket1 = summary.ByLoad.Single(b => b.BucketLabel == "1");
        Assert.Equal(2, bucket1.IntervalCount);
        Assert.Equal(2, bucket1.ReplyCount);
        Assert.Equal(30.0, bucket1.AverageFirstReplySeconds); // 60 / 2

        var bucket23 = summary.ByLoad.Single(b => b.BucketLabel == "2-3");
        Assert.Equal(2, bucket23.IntervalCount);            // load 3 across two days
        Assert.Equal(1, bucket23.ReplyCount);
        Assert.Equal(40.0, bucket23.AverageFirstReplySeconds); // 40 / 1 - the un-replied interval is not averaged

        var bucket68 = summary.ByLoad.Single(b => b.BucketLabel == "6-8");
        Assert.Equal(1, bucket68.IntervalCount);
        Assert.Equal(90.0, bucket68.AverageFirstReplySeconds);
    }

    [Fact]
    public async Task GetOperatorLoadReportAsync_ExcludesDaysOutsideTheWindow_AndOrdersOperatorsById()
    {
        await EnsureOperatorLoadTableAsync();
        var siteId = new SiteId(Guid.NewGuid());
        var opA = Guid.NewGuid();
        var opB = Guid.NewGuid();

        await using var connection = await fixture.DataSource.OpenConnectionAsync(CancellationToken.None);
        await InsertAsync(connection, siteId, Day1, opA, load: 1, intervals: 1, additional: 0, distinct: 1, replies: 0, replySecs: 0);
        await InsertAsync(connection, siteId, Day1, opB, load: 1, intervals: 1, additional: 0, distinct: 1, replies: 0, replySecs: 0);
        // Well before the window - must not be counted.
        await InsertAsync(connection, siteId, new DateOnly(2026, 1, 1), opA, load: 9, intervals: 99, additional: 99, distinct: 99, replies: 0, replySecs: 0);

        var result = await Store.GetOperatorLoadReportAsync(siteId, From, To, CancellationToken.None);

        Assert.Equal(2, result.Count);
        Assert.Equal(result.OrderBy(s => s.Operator.Value).Select(s => s.Operator).ToArray(), result.Select(s => s.Operator).ToArray());
        var a = result.Single(s => s.Operator.Value == opA);
        Assert.Equal(1, a.IntervalsHeld); // the out-of-window load-9 row is excluded
    }

    [Fact]
    public async Task GetOperatorLoadReportAsync_ForASiteWithNoRollups_ReturnsEmpty()
    {
        await EnsureOperatorLoadTableAsync();
        var result = await Store.GetOperatorLoadReportAsync(
            new SiteId(Guid.NewGuid()), From, To, CancellationToken.None);
        Assert.Empty(result);
    }

    private static async Task InsertAsync(
        NpgsqlConnection connection, SiteId siteId, DateOnly localDay, Guid operatorId,
        int load, long intervals, long additional, long distinct, long replies, long replySecs)
    {
        await connection.ExecuteAsync(
            """
            insert into analytics_operator_load_rollups
                (site_id, local_day, operator_id, concurrent_load,
                 interval_count, additional_interval_count, distinct_conversation_count,
                 reply_count, reply_seconds_sum, rebuilt_at)
            values
                (@SiteId, @LocalDay, @OperatorId, @Load,
                 @Intervals, @Additional, @Distinct, @Replies, @ReplySecs, now())
            """,
            new
            {
                SiteId = siteId.Value,
                // DateOnly is not accepted by Dapper's binder in this version - bind midnight, the same the
                // read store and the sibling rollup test do.
                LocalDay = localDay.ToDateTime(TimeOnly.MinValue),
                OperatorId = operatorId,
                Load = load,
                Intervals = intervals,
                Additional = additional,
                Distinct = distinct,
                Replies = replies,
                ReplySecs = replySecs,
            });
    }

    /// <summary>The operator-load rollup table from `26-237`'s ago-analytics schema - created here because
    /// ago-chat owns no migration for it (adr/0186). Idempotent so it is safe to call from every test.</summary>
    private async Task EnsureOperatorLoadTableAsync()
    {
        await using var connection = await fixture.DataSource.OpenConnectionAsync(CancellationToken.None);
        await connection.ExecuteAsync(
            """
            create table if not exists analytics_operator_load_rollups (
                site_id                     uuid        not null,
                local_day                   date        not null,
                operator_id                 uuid        not null,
                concurrent_load             integer     not null,
                interval_count              bigint      not null default 0,
                additional_interval_count   bigint      not null default 0,
                distinct_conversation_count bigint      not null default 0,
                reply_count                 bigint      not null default 0,
                reply_seconds_sum           bigint      not null default 0,
                rebuilt_at                  timestamptz not null,
                primary key (site_id, local_day, operator_id, concurrent_load)
            );
            """);
    }
}
