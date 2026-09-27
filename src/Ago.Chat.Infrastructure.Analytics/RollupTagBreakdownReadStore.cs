using Ago.Chat.Application.Abstractions;
using Ago.Chat.Domain;
using Dapper;

namespace Ago.Chat.Infrastructure.Analytics;

/// <summary>
/// `26-237`/`adr/0186` §8: the precomputed-rollup implementation of <see cref="ITagBreakdownReadStore"/>,
/// the same shape `26-223`'s <see cref="RollupOperatorAnalyticsReadStore"/> established. It returns the
/// identical <see cref="TagBreakdownResult"/> the live
/// <c>Ago.Chat.Infrastructure.Postgres.TagBreakdownReadStore</c> does, but reads a grouped range scan over
/// <c>analytics_daily_rollups</c> in <c>ago_analytics</c> instead of two <c>conversation_tags</c> joins on
/// the operational database: O(days), never O(conversations), and it never touches <c>ago_chat</c>.
///
/// <para><b>The coverage figure comes from the rollup's <c>tagged_conversation_count</c> measure, not from
/// summing the tag rows.</b> A conversation with several tags contributes to several <c>tag</c> rows, so
/// summing their <see cref="TagRollupRow.ConversationCount"/> would over-count it - exactly the trap the
/// live store avoids by running a separate <c>count(distinct ...)</c> query. The rollup's `26-237`
/// <c>tagged_conversation_count</c> on the <c>total</c> row is that distinct count, precomputed, so this
/// read takes <c>TotalConversationCount</c> and <c>TaggedConversationCount</c> straight off the total row
/// and needs no second query.</para>
///
/// <para><b>Per-tag conversion rate reconstituted, never stored</b> (design §7), <c>FollowUpNeeded</c>/
/// <c>Unset</c> excluded from the denominator, the identical `18-10`/`18-11` rule the live store applies.
/// The per-tag ranking is reproduced in C# exactly as the live store computes it (threshold-gated by
/// <see cref="AnalyticsOptions.MinimumSampleForRate"/>, then rate, then conversation count) - with one
/// documented difference in the <em>final</em> tie-break only: the live store's last key is the tag
/// <em>name</em> (ordinal), which is not available here because names are not stored in the rollup and are
/// resolved in the application layer (design §8.1); this read uses the tag <em>id</em> as the last key
/// instead, so the order is still fully deterministic and differs from the live store only when two tags
/// tie on threshold, rate <em>and</em> conversation count at once.</para>
///
/// <para><b>The tag name is left empty here and filled by the handler</b> through
/// <see cref="IAnalyticsLabelReadStore"/> - the same app-layer label merge design §8.1 mandates and
/// <see cref="RollupOperatorAnalyticsReadStore"/> uses for operator names. <see cref="TagBreakdownBucket.TagName"/>
/// is non-null by contract, so this store sets the empty string as a placeholder the handler replaces.</para>
/// </summary>
public sealed class RollupTagBreakdownReadStore(AnalyticsDbDataSource dataSource, AnalyticsOptions analyticsOptions)
    : ITagBreakdownReadStore
{
    private const string TagBreakdownSql = """
        select
            dimension_type                        as "DimensionType",
            dimension_key                         as "DimensionKey",
            sum(conversation_count)::bigint       as "ConversationCount",
            sum(tagged_conversation_count)::bigint as "TaggedConversationCount",
            sum(converted_count)::bigint          as "ConvertedCount",
            sum(not_converted_count)::bigint      as "NotConvertedCount"
        from analytics_daily_rollups
        where site_id = @SiteId
          and local_day >= @FromDay
          and local_day <= @ToDay
          and dimension_type in (@TotalDimension, @TagDimension)
        group by dimension_type, dimension_key
        """;

    public async Task<TagBreakdownResult> GetTagBreakdownAsync(
        SiteId siteId, DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken)
    {
        // Inclusive UTC-date bounds over local_day, DateTimeKind.Unspecified - see
        // RollupOperatorAnalyticsReadStore for the reasoning; the identical mapping.
        var fromDay = DateTime.SpecifyKind(from.UtcDateTime.Date, DateTimeKind.Unspecified);
        var toDay = DateTime.SpecifyKind(to.UtcDateTime.Date, DateTimeKind.Unspecified);

        await using var connection = await dataSource.Value.OpenConnectionAsync(cancellationToken);

        var rows = (await connection.QueryAsync<TagRollupRow>(new CommandDefinition(
            TagBreakdownSql,
            new
            {
                SiteId = siteId.Value,
                FromDay = fromDay,
                ToDay = toDay,
                TotalDimension = RollupDimensionTypes.Total,
                TagDimension = RollupDimensionTypes.Tag,
            },
            cancellationToken: cancellationToken))).ToList();

        // The total row carries both coverage numbers; absent (no rollup for the window) means an honest
        // zero coverage, PercentageTagged null - the same "nothing to compute a rate from yet" rule.
        var totalRow = rows.FirstOrDefault(r => r.DimensionType == RollupDimensionTypes.Total);
        var totalConversations = totalRow?.ConversationCount ?? 0;
        var taggedConversations = totalRow?.TaggedConversationCount ?? 0;
        double? percentageTagged = totalConversations == 0
            ? null
            : (double)taggedConversations / totalConversations;

        var byTag = rows
            .Where(r => r.DimensionType == RollupDimensionTypes.Tag && Guid.TryParse(r.DimensionKey, out _))
            .Select(r =>
            {
                var recorded = r.ConvertedCount + r.NotConvertedCount;
                double? rate = recorded == 0 ? null : (double)r.ConvertedCount / recorded;
                // TagName left empty - the handler resolves it via IAnalyticsLabelReadStore (design §8.1).
                return new TagBreakdownBucket(
                    new TagId(Guid.Parse(r.DimensionKey)),
                    string.Empty,
                    r.ConversationCount,
                    r.ConvertedCount,
                    r.NotConvertedCount,
                    recorded,
                    rate);
            })
            .OrderByDescending(b => MeetsSampleThreshold(b))
            .ThenByDescending(b => MeetsSampleThreshold(b) ? b.ConversionRate : null)
            .ThenByDescending(b => b.ConversationCount)
            .ThenBy(b => b.TagId.Value)
            .ToList();

        return new TagBreakdownResult(totalConversations, taggedConversations, percentageTagged, byTag);
    }

    private bool MeetsSampleThreshold(TagBreakdownBucket bucket) =>
        bucket.RecordedCount >= analyticsOptions.MinimumSampleForRate;
}
