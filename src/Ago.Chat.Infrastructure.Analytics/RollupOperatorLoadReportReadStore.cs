using Ago.Chat.Application.Abstractions;
using Ago.Chat.Domain;
using Dapper;

namespace Ago.Chat.Infrastructure.Analytics;

/// <summary>
/// `26-237`/`adr/0186` (operator-load rollup, decision B): the precomputed-rollup implementation of
/// <see cref="IOperatorLoadReportReadStore"/>, the same shape `26-223`'s
/// <see cref="RollupOperatorAnalyticsReadStore"/> and `26-237`'s <see cref="RollupConversionReportReadStore"/>
/// established. It answers the identical port question the live
/// <c>Ago.Chat.Infrastructure.Postgres.OperatorLoadReportReadStore</c> does - and returns the identical
/// <see cref="OperatorLoadSummary"/> shape - but instead of the O(N²) correlated-overlap scan over
/// <c>conversation_assignments</c> on the operational database (the «По сайту» screen's 499 cause), it
/// reads a grouped range scan over the narrow <c>analytics_operator_load_rollups</c> table in the dedicated
/// <c>ago_analytics</c> database: O(days), independent of total history, and it never touches
/// <c>conversation_assignments</c>, <c>messages</c>, or <c>ago_chat</c> at all.
///
/// <para><b>The overlap is resolved at interval close, not here or at aggregation time</b> (decision B).
/// AGO Chat's <c>ConversationAssignmentLog</c> stamps each interval's own concurrent-load (and capacity,
/// and first reply) onto the <c>ConversationAssignmentClosed</c> event at close, so the rollup keys on the
/// exact concurrent load and this read is a plain grouped sum. The bucketing into
/// <see cref="AnalyticsOptions.LoadBucketUpperBounds"/> stays a read-side C# fold (the identical fold the
/// live store applies), because the buckets are AGO Chat configuration - deliberately not known to the
/// ago-analytics writer.</para>
///
/// <para><b>Additive across days and loads.</b> Interval counts, the additional split, distinct
/// conversations and the decomposed reply metrics all sum; the reply-latency average is reconstituted
/// from <c>reply_seconds_sum / reply_count</c> per bucket, null when the denominator is zero - the same
/// "nothing to average yet, never zero, never a sentinel" rule the port documents.</para>
///
/// <para><b>Two accepted, documented divergences from the live store</b> (this epic's directive: all
/// reports read <c>ago_analytics</c>; ports/DTOs unchanged; `adr/0186` eventual consistency):
/// <list type="bullet">
/// <item><c>ConversationsHeld</c> sums each row's distinct-conversation count, so a conversation the same
/// operator held at two different start-loads (a transfer away and back at a changed load), or across two
/// tenant-local days, is counted once per such group rather than once window-globally - the same rare
/// transfer-away-and-back case <c>IntervalsHeld</c> already counts twice.</item>
/// <item>The window maps to <c>local_day</c> by the same UTC-date bound the sibling rollup reads use (the
/// unchanged port carries no tenant zone); within `adr/0186`'s accepted staleness. See
/// <see cref="RollupOperatorAnalyticsReadStore"/>'s remarks.</item>
/// </list></para>
///
/// <para>The operator <em>display name</em> is left <see langword="null"/> here (design §8.1: no
/// cross-database join); the site-analytics handler resolves it through <see cref="IAnalyticsLabelReadStore"/>
/// after merging this report in (<c>ResolveMissingOperatorNamesAsync</c>), exactly as it already does for
/// the null names the other rollup reads return.</para>
/// </summary>
public sealed class RollupOperatorLoadReportReadStore(AnalyticsDbDataSource dataSource, AnalyticsOptions analyticsOptions)
    : IOperatorLoadReportReadStore
{
    // A grouped range scan on the rollup primary key, summing each measure across the requested local days
    // per (operator, exact concurrent-load). `sum(bigint)` returns `numeric` in Postgres; cast back to
    // bigint so it maps to the long columns of OperatorLoadRollupRow. concurrent_load is a group key (int).
    private const string OperatorLoadSql = """
        select
            operator_id                          as "OperatorId",
            concurrent_load                      as "ConcurrentLoad",
            sum(interval_count)::bigint          as "IntervalCount",
            sum(additional_interval_count)::bigint as "AdditionalIntervalCount",
            sum(distinct_conversation_count)::bigint as "DistinctConversationCount",
            sum(reply_count)::bigint             as "ReplyCount",
            sum(reply_seconds_sum)::bigint       as "ReplySecondsSum"
        from analytics_operator_load_rollups
        where site_id = @SiteId
          and local_day >= @FromDay
          and local_day <= @ToDay
        group by operator_id, concurrent_load
        """;

    public async Task<IReadOnlyList<OperatorLoadSummary>> GetOperatorLoadReportAsync(
        SiteId siteId, DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken)
    {
        // The window bounds as calendar days at DateTimeKind.Unspecified so Npgsql binds them as `timestamp
        // without time zone` against the `date` column - see RollupOperatorAnalyticsReadStore for the full
        // reasoning; inclusive both ends, the identical mapping.
        var fromDay = DateTime.SpecifyKind(from.UtcDateTime.Date, DateTimeKind.Unspecified);
        var toDay = DateTime.SpecifyKind(to.UtcDateTime.Date, DateTimeKind.Unspecified);

        await using var connection = await dataSource.Value.OpenConnectionAsync(cancellationToken);

        var rows = (await connection.QueryAsync<OperatorLoadRollupRow>(new CommandDefinition(
            OperatorLoadSql,
            new { SiteId = siteId.Value, FromDay = fromDay, ToDay = toDay },
            cancellationToken: cancellationToken))).ToList();

        var bounds = analyticsOptions.LoadBucketUpperBounds;

        return rows
            .GroupBy(r => r.OperatorId)
            .Select(operatorRows => ToSummary(operatorRows.Key, operatorRows, bounds))
            .OrderBy(s => s.Operator.Value)
            .ToList();
    }

    private static OperatorLoadSummary ToSummary(
        Guid operatorId, IEnumerable<OperatorLoadRollupRow> rows, IReadOnlyList<int> bounds)
    {
        long intervalsHeld = 0;
        long additionalIntervals = 0;
        long conversationsHeld = 0;

        // Fold the exact concurrent-load rows into the configured buckets - the identical fold the live
        // store applies, in C#, so a label printed on a report always agrees with the bucket that produced
        // it (OperatorLoadBuckets is the single definition of both).
        var byBucketIndex = new SortedDictionary<int, (long IntervalCount, long ReplyCount, long ReplySecondsSum)>();

        foreach (var row in rows)
        {
            intervalsHeld += row.IntervalCount;
            additionalIntervals += row.AdditionalIntervalCount;
            conversationsHeld += row.DistinctConversationCount;

            var index = OperatorLoadBuckets.IndexOf(bounds, row.ConcurrentLoad);
            var existing = byBucketIndex.TryGetValue(index, out var value) ? value : default;
            byBucketIndex[index] = (
                existing.IntervalCount + row.IntervalCount,
                existing.ReplyCount + row.ReplyCount,
                existing.ReplySecondsSum + row.ReplySecondsSum);
        }

        var byLoad = byBucketIndex
            .Select(kvp => new OperatorLoadBucketEntry(
                OperatorLoadBuckets.Label(bounds, kvp.Key),
                kvp.Value.IntervalCount,
                kvp.Value.ReplyCount,
                kvp.Value.ReplyCount > 0 ? (double)kvp.Value.ReplySecondsSum / kvp.Value.ReplyCount : null))
            .ToList();

        return new OperatorLoadSummary(
            new OperatorId(operatorId),
            // Resolved by the handler via IAnalyticsLabelReadStore (design §8.1: no cross-database join).
            null,
            conversationsHeld,
            intervalsHeld,
            intervalsHeld - additionalIntervals,
            additionalIntervals,
            byLoad);
    }
}
