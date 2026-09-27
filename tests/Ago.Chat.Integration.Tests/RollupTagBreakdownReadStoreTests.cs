using Ago.Chat.Application.Abstractions;
using Ago.Chat.Domain;
using Ago.Chat.Infrastructure.Analytics;
using Dapper;
using Npgsql;

namespace Ago.Chat.Integration.Tests;

/// <summary>
/// `26-237`/`adr/0186` §8: the tag-breakdown read switch, against a real Postgres standing in for the
/// <c>ago_analytics</c> rollup database. Proves <see cref="RollupTagBreakdownReadStore"/> takes the site-wide
/// coverage (all conversations and the distinct-tagged subset) straight off the <c>total</c> row's
/// <c>conversation_count</c>/<c>tagged_conversation_count</c> (the `26-237` measure) - never by summing the
/// per-tag rows, which would double-count a multi-tag conversation - and maps the <c>tag</c> rows to the
/// per-tag buckets with the rate reconstituted and the tag name left empty for the application-layer merge.
/// </summary>
[Collection(PostgresCollection.Name)]
public class RollupTagBreakdownReadStoreTests(PostgresFixture fixture)
{
    private static readonly DateOnly Day1 = new(2026, 6, 10);
    private static readonly DateOnly Day2 = new(2026, 6, 11);
    private static readonly DateTimeOffset From = new(2026, 6, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset To = new(2026, 6, 30, 12, 0, 0, TimeSpan.Zero);

    private RollupTagBreakdownReadStore StoreWithThreshold(int minimumSampleForRate) =>
        new(new AnalyticsDbDataSource(fixture.DataSource), new AnalyticsOptions { MinimumSampleForRate = minimumSampleForRate });

    [Fact]
    public async Task GetTagBreakdownAsync_TakesCoverageFromTheTotalRow_AndPerTagCountsFromTheTagRows()
    {
        await EnsureAnalyticsTablesAsync();
        var siteId = new SiteId(Guid.NewGuid());
        var billing = Guid.NewGuid();
        var refund = Guid.NewGuid();

        await using var connection = await fixture.DataSource.OpenConnectionAsync(CancellationToken.None);
        // total across two days: 10 conversations, 6 of them carrying at least one tag (counted once each).
        // The per-tag rows below sum to 4 + 3 = 7 > 6 on purpose: a multi-tag conversation is in two tag rows
        // but one tagged conversation, which is exactly why coverage comes from the total row's own measure.
        await InsertAsync(connection, siteId, Day1, "total", "", conversationCount: 6, tagged: 4, converted: 0, notConverted: 0);
        await InsertAsync(connection, siteId, Day2, "total", "", conversationCount: 4, tagged: 2, converted: 0, notConverted: 0);

        await InsertAsync(connection, siteId, Day1, "tag", billing.ToString(), conversationCount: 4, tagged: 4, converted: 3, notConverted: 1);
        await InsertAsync(connection, siteId, Day1, "tag", refund.ToString(), conversationCount: 3, tagged: 3, converted: 1, notConverted: 1);

        var result = await StoreWithThreshold(1).GetTagBreakdownAsync(siteId, From, To, CancellationToken.None);

        Assert.Equal(10, result.TotalConversationCount);
        Assert.Equal(6, result.TaggedConversationCount);
        Assert.Equal(0.6, result.PercentageTagged!.Value, 3);

        // Both meet the threshold (recorded >= 1); billing (rate 0.75) ranks above refund (0.5).
        Assert.Equal(2, result.ByTag.Count);
        Assert.Equal(billing, result.ByTag[0].TagId.Value);
        Assert.Equal(4, result.ByTag[0].ConversationCount);
        Assert.Equal(0.75, result.ByTag[0].ConversionRate!.Value, 3);
        Assert.Equal(refund, result.ByTag[1].TagId.Value);
        // The tag name is resolved in the application-layer merge (design §8.1), never in the store.
        Assert.All(result.ByTag, b => Assert.Equal(string.Empty, b.TagName));
    }

    [Fact]
    public async Task GetTagBreakdownAsync_WithNoRollupsForTheSite_ReturnsZeroCoverage_NullPercentage_NoTags()
    {
        await EnsureAnalyticsTablesAsync();

        var result = await StoreWithThreshold(1)
            .GetTagBreakdownAsync(new SiteId(Guid.NewGuid()), From, To, CancellationToken.None);

        Assert.Equal(0, result.TotalConversationCount);
        Assert.Equal(0, result.TaggedConversationCount);
        Assert.Null(result.PercentageTagged);
        Assert.Empty(result.ByTag);
    }

    private static async Task InsertAsync(
        NpgsqlConnection connection, SiteId siteId, DateOnly localDay, string dimensionType, string dimensionKey,
        long conversationCount, long tagged, long converted, long notConverted)
    {
        await connection.ExecuteAsync(
            """
            insert into analytics_daily_rollups
                (site_id, local_day, dimension_type, dimension_key,
                 conversation_count, tagged_conversation_count, converted_count, not_converted_count, rebuilt_at)
            values
                (@SiteId, @LocalDay, @DimensionType, @DimensionKey,
                 @Cc, @Tagged, @Converted, @NotConverted, now())
            """,
            new
            {
                SiteId = siteId.Value,
                LocalDay = localDay.ToDateTime(TimeOnly.MinValue),
                DimensionType = dimensionType,
                DimensionKey = dimensionKey,
                Cc = conversationCount,
                Tagged = tagged,
                Converted = converted,
                NotConverted = notConverted,
            });
    }

    /// <summary>The `ago_analytics` rollup table including the `26-237` <c>tagged_conversation_count</c>
    /// column. ADD COLUMN IF NOT EXISTS keeps it safe against a sibling test in the shared fixture that
    /// created the table from an earlier column set.</summary>
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
