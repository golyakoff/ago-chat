using Ago.Chat.Application.Abstractions;
using Ago.Chat.Domain;
using Ago.Chat.Infrastructure.Analytics;
using Dapper;
using Npgsql;

namespace Ago.Chat.Integration.Tests;

/// <summary>
/// `26-237`/`adr/0186` (module-flow funnel rollup): the funnel read switch, against a real Postgres
/// standing in for the <c>ago_analytics</c> rollup database. Proves <see cref="RollupModuleFlowReadStore"/>
/// sums the per-day funnel counts back to the identical <see cref="ModuleFlowReportResult"/> the live store
/// returns - across the window, for the one configured module key, with an honest zero for an empty window.
/// ago-chat owns no migration for this schema (adr/0186), so this test creates the table itself from the
/// funnel rollup DDL and reads it through the production Dapper adapter.
/// </summary>
[Collection(PostgresCollection.Name)]
public class RollupModuleFlowReadStoreTests(PostgresFixture fixture)
{
    private static readonly DateOnly Day1 = new(2026, 6, 10);
    private static readonly DateOnly Day2 = new(2026, 6, 11);
    private static readonly DateTimeOffset From = new(2026, 6, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset To = new(2026, 6, 30, 12, 0, 0, TimeSpan.Zero);
    private static readonly ModuleKey Calendar = new("calendar");

    private RollupModuleFlowReadStore Store() => new(new AnalyticsDbDataSource(fixture.DataSource));

    [Fact]
    public async Task GetSiteModuleFlowReportAsync_SumsStartedAndClosedAcrossDays_ForTheRequestedModuleKey()
    {
        await EnsureAnalyticsTablesAsync();
        var siteId = new SiteId(Guid.NewGuid());

        await using var connection = await fixture.DataSource.OpenConnectionAsync(CancellationToken.None);
        await InsertAsync(connection, siteId, Day1, "calendar", started: 5, closed: 3);
        await InsertAsync(connection, siteId, Day2, "calendar", started: 4, closed: 2);
        // A different module key on the same site - must not leak into the calendar report.
        await InsertAsync(connection, siteId, Day1, "survey", started: 9, closed: 9);

        var result = await Store().GetSiteModuleFlowReportAsync(siteId, Calendar, From, To, CancellationToken.None);

        Assert.Equal(9, result.FlowsStarted); // 5 + 4
        Assert.Equal(5, result.FlowsClosed);  // 3 + 2
    }

    [Fact]
    public async Task GetSiteModuleFlowReportAsync_ExcludesDaysOutsideTheWindow_AndOtherSites()
    {
        await EnsureAnalyticsTablesAsync();
        var siteId = new SiteId(Guid.NewGuid());

        await using var connection = await fixture.DataSource.OpenConnectionAsync(CancellationToken.None);
        await InsertAsync(connection, siteId, Day1, "calendar", started: 6, closed: 4);
        // Far outside the window - must not be counted.
        await InsertAsync(connection, siteId, new DateOnly(2026, 1, 1), "calendar", started: 99, closed: 99);
        // Another site - must not be counted.
        await InsertAsync(connection, new SiteId(Guid.NewGuid()), Day1, "calendar", started: 77, closed: 77);

        var result = await Store().GetSiteModuleFlowReportAsync(siteId, Calendar, From, To, CancellationToken.None);

        Assert.Equal(6, result.FlowsStarted);
        Assert.Equal(4, result.FlowsClosed);
    }

    [Fact]
    public async Task GetSiteModuleFlowReportAsync_ReturnsAnHonestZeroForAWindowWithNoRollup()
    {
        await EnsureAnalyticsTablesAsync();

        var result = await Store()
            .GetSiteModuleFlowReportAsync(new SiteId(Guid.NewGuid()), Calendar, From, To, CancellationToken.None);

        Assert.Equal(0, result.FlowsStarted);
        Assert.Equal(0, result.FlowsClosed);
    }

    private static async Task InsertAsync(
        NpgsqlConnection connection, SiteId siteId, DateOnly localDay, string moduleKey, long started, long closed)
    {
        await connection.ExecuteAsync(
            """
            insert into analytics_module_flow_rollups
                (site_id, local_day, module_key, flows_started_count, flows_closed_count, rebuilt_at)
            values
                (@SiteId, @LocalDay, @ModuleKey, @Started, @Closed, now())
            """,
            new
            {
                SiteId = siteId.Value,
                LocalDay = localDay.ToDateTime(TimeOnly.MinValue),
                ModuleKey = moduleKey,
                Started = started,
                Closed = closed,
            });
    }

    /// <summary>The `ago_analytics` funnel rollup table from the `26-237` migration DDL. Idempotent, so it
    /// is safe against a sibling test in the shared fixture that already created it.</summary>
    private async Task EnsureAnalyticsTablesAsync()
    {
        await using var connection = await fixture.DataSource.OpenConnectionAsync(CancellationToken.None);
        await connection.ExecuteAsync(
            """
            create table if not exists analytics_module_flow_rollups (
                site_id             uuid        not null,
                local_day           date        not null,
                module_key          varchar(64) not null,
                flows_started_count bigint      not null default 0,
                flows_closed_count  bigint      not null default 0,
                rebuilt_at          timestamptz not null,
                primary key (site_id, local_day, module_key)
            );
            """);
    }
}
