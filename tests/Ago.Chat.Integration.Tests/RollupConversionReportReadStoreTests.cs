using Ago.Chat.Application.Abstractions;
using Ago.Chat.Domain;
using Ago.Chat.Infrastructure.Analytics;
using Dapper;
using Npgsql;

namespace Ago.Chat.Integration.Tests;

/// <summary>
/// `26-237`/`adr/0186` §8: the conversion read switch, against a real Postgres standing in for the
/// <c>ago_analytics</c> rollup database. Proves <see cref="RollupConversionReportReadStore"/> maps the
/// <c>total</c>/<c>operator</c> rollup dimensions back to the identical <see cref="ConversionReportResult"/>
/// the live store returns - summed across every day in the window, with the rate reconstituted from the
/// stored outcome counts (<c>FollowUpNeeded</c>/<c>Unset</c> excluded from the denominator), the per-operator
/// ranking gated by <see cref="AnalyticsOptions.MinimumSampleForRate"/>, and the operator name left null for
/// the application-layer merge (design §8.1). ago-chat owns no migration for this schema (adr/0186), so this
/// test creates the table itself from the design's §7 DDL and reads it through the production Dapper adapter.
/// </summary>
[Collection(PostgresCollection.Name)]
public class RollupConversionReportReadStoreTests(PostgresFixture fixture)
{
    private static readonly DateOnly Day1 = new(2026, 6, 10);
    private static readonly DateOnly Day2 = new(2026, 6, 11);
    private static readonly DateTimeOffset From = new(2026, 6, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset To = new(2026, 6, 30, 12, 0, 0, TimeSpan.Zero);

    private RollupConversionReportReadStore StoreWithThreshold(int minimumSampleForRate) =>
        new(new AnalyticsDbDataSource(fixture.DataSource), new AnalyticsOptions { MinimumSampleForRate = minimumSampleForRate });

    [Fact]
    public async Task GetConversionReportAsync_SumsOutcomesAcrossDays_ReconstitutesRate_ExcludingFollowUpAndUnset()
    {
        await EnsureAnalyticsTablesAsync();
        var siteId = new SiteId(Guid.NewGuid());

        await using var connection = await fixture.DataSource.OpenConnectionAsync(CancellationToken.None);
        // total, summed across two days: converted 3, notConverted 2, followUp 1, unset 1 ->
        // recorded 5, rate 3/5 = 0.6 (followUp + unset excluded from the denominator).
        await InsertAsync(connection, siteId, Day1, "total", "", converted: 2, notConverted: 1, followUp: 0, unset: 1);
        await InsertAsync(connection, siteId, Day2, "total", "", converted: 1, notConverted: 1, followUp: 1, unset: 0);

        var result = await StoreWithThreshold(1).GetConversionReportAsync(siteId, From, To, CancellationToken.None);

        Assert.Equal(3, result.Overall.ConvertedCount);
        Assert.Equal(2, result.Overall.NotConvertedCount);
        Assert.Equal(1, result.Overall.FollowUpNeededCount);
        Assert.Equal(1, result.Overall.UnsetCount);
        Assert.Equal(5, result.Overall.RecordedCount);
        Assert.Equal(0.6, result.Overall.ConversionRate!.Value, 3);
    }

    [Fact]
    public async Task GetConversionReportAsync_RanksOperatorsByRate_AboveTheThreshold_AndLeavesNamesForTheAppLayer()
    {
        await EnsureAnalyticsTablesAsync();
        var siteId = new SiteId(Guid.NewGuid());
        var operatorA = Guid.NewGuid();
        var operatorB = Guid.NewGuid();

        await using var connection = await fixture.DataSource.OpenConnectionAsync(CancellationToken.None);
        // A across two days: converted 2 + 0, notConverted 0 + 1 -> converted 2, recorded 3, rate 0.667.
        await InsertAsync(connection, siteId, Day1, "operator", operatorA.ToString(), converted: 2, notConverted: 0, followUp: 0, unset: 0);
        await InsertAsync(connection, siteId, Day2, "operator", operatorA.ToString(), converted: 0, notConverted: 1, followUp: 0, unset: 0);
        // B: converted 1, notConverted 1 -> recorded 2, rate 0.5.
        await InsertAsync(connection, siteId, Day1, "operator", operatorB.ToString(), converted: 1, notConverted: 1, followUp: 0, unset: 0);

        var result = await StoreWithThreshold(1).GetConversionReportAsync(siteId, From, To, CancellationToken.None);

        Assert.Equal(2, result.ByOperator.Count);
        // Both meet the sample threshold (recorded >= 1), so they rank by rate descending: A (0.667) before B (0.5).
        Assert.Equal(operatorA, result.ByOperator[0].Operator.Value);
        Assert.Equal(3, result.ByOperator[0].Bucket.RecordedCount);
        Assert.Equal(0.667, result.ByOperator[0].Bucket.ConversionRate!.Value, 3);
        Assert.Equal(operatorB, result.ByOperator[1].Operator.Value);
        // The display name is resolved in the application-layer merge (design §8.1), never in the store.
        Assert.All(result.ByOperator, o => Assert.Null(o.OperatorName));
    }

    [Fact]
    public async Task GetConversionReportAsync_ExcludesDaysOutsideTheWindow_AndReturnsAnHonestZeroForAnEmptySite()
    {
        await EnsureAnalyticsTablesAsync();
        var siteId = new SiteId(Guid.NewGuid());

        await using var connection = await fixture.DataSource.OpenConnectionAsync(CancellationToken.None);
        await InsertAsync(connection, siteId, Day1, "total", "", converted: 3, notConverted: 1, followUp: 0, unset: 0);
        // Far outside the window - must not be counted.
        await InsertAsync(connection, siteId, new DateOnly(2026, 1, 1), "total", "", converted: 99, notConverted: 99, followUp: 0, unset: 0);

        var inWindow = await StoreWithThreshold(1).GetConversionReportAsync(siteId, From, To, CancellationToken.None);
        Assert.Equal(3, inWindow.Overall.ConvertedCount);
        Assert.Equal(1, inWindow.Overall.NotConvertedCount);

        var emptyResult = await StoreWithThreshold(1)
            .GetConversionReportAsync(new SiteId(Guid.NewGuid()), From, To, CancellationToken.None);
        Assert.Equal(0, emptyResult.Overall.ConvertedCount);
        Assert.Null(emptyResult.Overall.ConversionRate);
        Assert.Empty(emptyResult.ByOperator);
    }

    private static async Task InsertAsync(
        NpgsqlConnection connection, SiteId siteId, DateOnly localDay, string dimensionType, string dimensionKey,
        long converted, long notConverted, long followUp, long unset)
    {
        await connection.ExecuteAsync(
            """
            insert into analytics_daily_rollups
                (site_id, local_day, dimension_type, dimension_key,
                 conversation_count, converted_count, not_converted_count, follow_up_count, unset_count, rebuilt_at)
            values
                (@SiteId, @LocalDay, @DimensionType, @DimensionKey,
                 @Cc, @Converted, @NotConverted, @FollowUp, @Unset, now())
            """,
            new
            {
                SiteId = siteId.Value,
                LocalDay = localDay.ToDateTime(TimeOnly.MinValue),
                DimensionType = dimensionType,
                DimensionKey = dimensionKey,
                Cc = converted + notConverted + followUp + unset,
                Converted = converted,
                NotConverted = notConverted,
                FollowUp = followUp,
                Unset = unset,
            });
    }

    /// <summary>The `ago_analytics` rollup table from the design's §7 DDL, including the `26-237`
    /// <c>tagged_conversation_count</c> column. Idempotent, and the ADD COLUMN IF NOT EXISTS makes it safe
    /// against a sibling test in the shared fixture that created the table from an earlier column set.</summary>
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
                tagged_conversation_count  bigint      not null default 0,
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
            alter table analytics_daily_rollups
                add column if not exists tagged_conversation_count bigint not null default 0;
            """);
    }
}
