using Ago.Chat.Domain;
using Ago.Chat.Infrastructure.Analytics;
using Dapper;
using Npgsql;

namespace Ago.Chat.Integration.Tests;

/// <summary>
/// `26-223`/`adr/0186` (`docs/design/analytics-precompute.md` §8): the analytics read switch, against a
/// real Postgres standing in for the <c>ago_analytics</c> rollup database. Proves the precomputed-rollup
/// implementation of <see cref="Ago.Chat.Application.Abstractions.IOperatorAnalyticsReadStore"/> maps
/// <c>analytics_daily_rollups</c> back to the identical report DTO the live store returns - including the
/// load-bearing detail that averages are reconstituted from the stored sums and counts (never stored as
/// ratios), summed across every day in the window, and that an empty window yields a well-formed empty
/// report rather than a crash. Also proves <see cref="AnalyticsFreshnessReadStore"/> returns the last
/// successful rollup run's completion instant behind <c>computedAsOf</c>.
///
/// <para>ago-chat does not own the <c>ago_analytics</c> schema (the standalone ago-analytics service does,
/// adr/0186), so this test creates the two tables itself from the design's own §7 DDL and reads them
/// through the same Dapper adapter production uses - the read contract, not a migration.</para>
/// </summary>
[Collection(PostgresCollection.Name)]
public class RollupOperatorAnalyticsReadStoreTests(PostgresFixture fixture)
{
    private static readonly DateOnly Day1 = new(2026, 6, 10);
    private static readonly DateOnly Day2 = new(2026, 6, 11);

    // A window whose UTC-date bounds cover both rollup days (inclusive), the mapping the read store applies.
    private static readonly DateTimeOffset From = new(2026, 6, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset To = new(2026, 6, 30, 12, 0, 0, TimeSpan.Zero);

    private RollupOperatorAnalyticsReadStore Store => new(new AnalyticsDbDataSource(fixture.DataSource));

    private AnalyticsFreshnessReadStore Freshness => new(new AnalyticsDbDataSource(fixture.DataSource));

    [Fact]
    public async Task GetSiteAnalyticsAsync_MapsRollupsToTheReportDto_ReconstitutingAveragesFromSumsAndCounts_AcrossDays()
    {
        await EnsureAnalyticsTablesAsync();
        var siteId = new SiteId(Guid.NewGuid());
        var operatorId = Guid.NewGuid();

        await using var connection = await fixture.DataSource.OpenConnectionAsync(CancellationToken.None);

        // total: summed across two days -> cc 6, missed 1, first-response 300s/4 = 75.0, duration 1000s/4 = 250.0
        await InsertRollupAsync(connection, siteId, Day1, "total", "", cc: 4, missed: 1, frSum: 180, frCount: 3, durSum: 600, durCount: 2);
        await InsertRollupAsync(connection, siteId, Day2, "total", "", cc: 2, missed: 0, frSum: 120, frCount: 1, durSum: 400, durCount: 2);

        // channel Widget: cc 4, first-response 180/2 = 90.0, duration 300/2 = 150.0
        await InsertRollupAsync(connection, siteId, Day1, "channel", "Widget", cc: 3, missed: 1, frSum: 180, frCount: 2, durSum: 200, durCount: 1);
        await InsertRollupAsync(connection, siteId, Day2, "channel", "Widget", cc: 1, missed: 0, frSum: 0, frCount: 0, durSum: 100, durCount: 1);
        // channel Sms: cc 2 but never answered -> AverageFirstResponseSeconds null (denominator 0)
        await InsertRollupAsync(connection, siteId, Day1, "channel", "Sms", cc: 2, missed: 2, frSum: 0, frCount: 0, durSum: 0, durCount: 0);

        await InsertRollupAsync(connection, siteId, Day1, "operator", operatorId.ToString(), cc: 5, missed: 0, frSum: 250, frCount: 5, durSum: 500, durCount: 5);
        await InsertRollupAsync(connection, siteId, Day1, "referrer", "Direct", cc: 3, missed: 0, frSum: 0, frCount: 0, durSum: 0, durCount: 0);
        await InsertRollupAsync(connection, siteId, Day1, "campaign", "summer_sale", cc: 1, missed: 0, frSum: 30, frCount: 1, durSum: 100, durCount: 1);

        var result = await Store.GetSiteAnalyticsAsync(siteId, From, To, CancellationToken.None);

        Assert.Equal(6, result.Overall.ConversationCount);
        Assert.Equal(1, result.Overall.MissedCount);
        Assert.Equal(75.0, result.Overall.AverageFirstResponseSeconds);
        Assert.Equal(250.0, result.Overall.AverageDurationSeconds);

        var widget = result.ByChannel.Single(c => c.Channel == "Widget");
        Assert.Equal(4, widget.Bucket.ConversationCount);
        Assert.Equal(90.0, widget.Bucket.AverageFirstResponseSeconds);
        Assert.Equal(150.0, widget.Bucket.AverageDurationSeconds);

        var sms = result.ByChannel.Single(c => c.Channel == "Sms");
        Assert.Equal(2, sms.Bucket.ConversationCount);
        Assert.Null(sms.Bucket.AverageFirstResponseSeconds);
        Assert.Null(sms.Bucket.AverageDurationSeconds);

        // Channels are ordered by ordinal label - "Sms" before "Widget".
        Assert.Equal(["Sms", "Widget"], result.ByChannel.Select(c => c.Channel).ToArray());

        var op = Assert.Single(result.ByOperator);
        Assert.Equal(operatorId, op.Operator.Value);
        Assert.Equal(5, op.Bucket.ConversationCount);
        Assert.Equal(50.0, op.Bucket.AverageFirstResponseSeconds);
        // Operator display name is not stored in the rollups (design §8.1: no cross-database join) - it is
        // resolved in the application-layer merge, so this store leaves it null.
        Assert.Null(op.OperatorName);

        var referrer = Assert.Single(result.ByReferrer);
        Assert.Equal("Direct", referrer.ReferrerHost);
        Assert.Equal(3, referrer.Bucket.ConversationCount);

        var campaign = Assert.Single(result.ByCampaign);
        Assert.Equal("summer_sale", campaign.UtmCampaign);
        Assert.Equal(1, campaign.Bucket.ConversationCount);
    }

    [Fact]
    public async Task GetSiteAnalyticsAsync_ExcludesDaysOutsideTheRequestedWindow()
    {
        await EnsureAnalyticsTablesAsync();
        var siteId = new SiteId(Guid.NewGuid());

        await using var connection = await fixture.DataSource.OpenConnectionAsync(CancellationToken.None);
        // In window.
        await InsertRollupAsync(connection, siteId, Day1, "total", "", cc: 4, missed: 0, frSum: 0, frCount: 0, durSum: 0, durCount: 0);
        // Well before the window - must not be counted.
        await InsertRollupAsync(connection, siteId, new DateOnly(2026, 1, 1), "total", "", cc: 99, missed: 9, frSum: 0, frCount: 0, durSum: 0, durCount: 0);

        var result = await Store.GetSiteAnalyticsAsync(siteId, From, To, CancellationToken.None);

        Assert.Equal(4, result.Overall.ConversationCount);
        Assert.Equal(0, result.Overall.MissedCount);
    }

    [Fact]
    public async Task GetSiteAnalyticsAsync_WithNoRollupsForTheSite_ReturnsAWellFormedEmptyReport_NotACrash()
    {
        await EnsureAnalyticsTablesAsync();
        var emptySite = new SiteId(Guid.NewGuid());

        var result = await Store.GetSiteAnalyticsAsync(emptySite, From, To, CancellationToken.None);

        Assert.Equal(0, result.Overall.ConversationCount);
        Assert.Null(result.Overall.AverageFirstResponseSeconds);
        Assert.Null(result.Overall.AverageDurationSeconds);
        Assert.Equal(0, result.Overall.MissedCount);
        Assert.Empty(result.ByChannel);
        Assert.Empty(result.ByOperator);
        Assert.Empty(result.ByReferrer);
        Assert.Empty(result.ByCampaign);
    }

    [Fact]
    public async Task GetLastRollupCompletedAtAsync_ReturnsTheMostRecentSuccessfulRun_OrNullWhenNoneHaveRun()
    {
        await EnsureAnalyticsTablesAsync();

        await using var connection = await fixture.DataSource.OpenConnectionAsync(CancellationToken.None);
        // Isolate this global (site-agnostic) table from any leftover rows.
        await connection.ExecuteAsync("truncate table analytics_rollup_runs");

        Assert.Null(await Freshness.GetLastRollupCompletedAtAsync(CancellationToken.None));

        var older = new DateTimeOffset(2026, 6, 11, 8, 0, 0, TimeSpan.Zero);
        var newer = new DateTimeOffset(2026, 6, 11, 9, 0, 0, TimeSpan.Zero);
        await connection.ExecuteAsync(
            "insert into analytics_rollup_runs (completed_at, newest_local_day) values (@C, @D)",
            new[]
            {
                new { C = older, D = Day1.ToDateTime(TimeOnly.MinValue) },
                new { C = newer, D = Day2.ToDateTime(TimeOnly.MinValue) },
            });

        var lastRun = await Freshness.GetLastRollupCompletedAtAsync(CancellationToken.None);

        Assert.Equal(newer, lastRun);
    }

    private static async Task InsertRollupAsync(
        NpgsqlConnection connection, SiteId siteId, DateOnly localDay, string dimensionType, string dimensionKey,
        long cc, long missed, long frSum, long frCount, long durSum, long durCount)
    {
        await connection.ExecuteAsync(
            """
            insert into analytics_daily_rollups
                (site_id, local_day, dimension_type, dimension_key,
                 conversation_count, first_response_seconds_sum, first_response_count,
                 duration_seconds_sum, duration_count, missed_count, rebuilt_at)
            values
                (@SiteId, @LocalDay, @DimensionType, @DimensionKey,
                 @Cc, @FrSum, @FrCount, @DurSum, @DurCount, @Missed, now())
            """,
            new
            {
                SiteId = siteId.Value,
                // DateOnly is not accepted by Dapper's parameter binder in this version - bind the date as
                // a midnight DateTime (the same reason the read store does), which Npgsql stores into the
                // `date` column unchanged.
                LocalDay = localDay.ToDateTime(TimeOnly.MinValue),
                DimensionType = dimensionType,
                DimensionKey = dimensionKey,
                Cc = cc,
                FrSum = frSum,
                FrCount = frCount,
                DurSum = durSum,
                DurCount = durCount,
                Missed = missed,
            });
    }

    /// <summary>The `ago_analytics` schema from the design's §7 DDL - created here because ago-chat owns no
    /// migration for it (adr/0186: the standalone ago-analytics service does). Idempotent (`if not exists`)
    /// so it is safe to call from every test in this class against the shared fixture database. The
    /// conversion columns (design §7, consumed by the separate conversion read switch) carry DB defaults so
    /// this slice's inserts never mention them.</summary>
    private async Task EnsureAnalyticsTablesAsync()
    {
        await using var connection = await fixture.DataSource.OpenConnectionAsync(CancellationToken.None);
        await connection.ExecuteAsync(
            """
            create table if not exists analytics_daily_rollups (
                site_id                    uuid        not null,
                local_day                  date        not null,
                dimension_type             text        not null,
                dimension_key              text        not null,
                conversation_count         bigint      not null default 0,
                first_response_seconds_sum bigint      not null default 0,
                first_response_count       bigint      not null default 0,
                duration_seconds_sum       bigint      not null default 0,
                duration_count             bigint      not null default 0,
                missed_count               bigint      not null default 0,
                converted_count            bigint      not null default 0,
                not_converted_count        bigint      not null default 0,
                follow_up_count            bigint      not null default 0,
                unset_count                bigint      not null default 0,
                recorded_count             bigint      not null default 0,
                rebuilt_at                 timestamptz not null,
                primary key (site_id, local_day, dimension_type, dimension_key)
            );

            create table if not exists analytics_rollup_runs (
                id               bigint generated always as identity primary key,
                completed_at     timestamptz not null,
                newest_local_day date        not null
            );
            """);
    }
}
