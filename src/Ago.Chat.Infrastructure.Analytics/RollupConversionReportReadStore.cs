using Ago.Chat.Application.Abstractions;
using Ago.Chat.Domain;
using Dapper;

namespace Ago.Chat.Infrastructure.Analytics;

/// <summary>
/// `26-237`/`adr/0186` §8: the precomputed-rollup implementation of <see cref="IConversionReportReadStore"/>,
/// the same shape `26-223`'s <see cref="RollupOperatorAnalyticsReadStore"/> established for site-analytics.
/// It answers the identical port question the live
/// <c>Ago.Chat.Infrastructure.Postgres.ConversionReportReadStore</c> does - and returns the identical
/// <see cref="ConversionReportResult"/> - but instead of a <c>GROUPING SETS</c> scan of <c>conversations</c>
/// on the operational database on every request, it reads a grouped range scan over the narrow
/// <c>analytics_daily_rollups</c> table in the dedicated <c>ago_analytics</c> database: O(days), independent
/// of total history, and it never touches <c>ago_chat</c> at all.
///
/// <para><b>Operator attribution differs from the live store, by construction (documented, not accidental).</b>
/// The live store attributes an outcome to <c>conversations.operator_id</c> (the conversation's assigned
/// operator). The rollup's <c>operator</c> dimension is keyed by the <em>first-responding</em> operator -
/// the same attribution <see cref="IOperatorAnalyticsReadStore"/>/site-analytics already uses (design §7),
/// since the rollup is folded once per conversation under the operator of its first operator message. For a
/// conversation handled by one operator the two agree; for a transferred conversation they can differ. This
/// is an accepted consequence of serving every report from one consistent rollup (this epic's directive:
/// all reports read <c>ago_analytics</c>; ports/DTOs unchanged), and it makes the conversion per-operator
/// breakdown consistent with the site-analytics per-operator breakdown rather than divergent from it.</para>
///
/// <para><b>Rate reconstituted, never stored</b> (design §7): the rollup keeps the decomposed outcome counts
/// and this read divides <c>converted / (converted + not_converted)</c> - <see langword="null"/> when that
/// denominator is zero, the identical "never zero, never a sentinel" rule
/// <see cref="ConversionBucket.ConversionRate"/> documents. <c>FollowUpNeeded</c> and <c>Unset</c> are
/// excluded from the denominator exactly as the live store excludes them.</para>
///
/// <para><b>Ranking and the operator name are unchanged from the live store's own contract.</b> The
/// per-operator ranking (threshold-gated by <see cref="AnalyticsOptions.MinimumSampleForRate"/>, then rate,
/// then recorded count, then operator id) is reproduced here in C# exactly as the live store computes it -
/// its final tie-break is the operator id in both, so the order is identical, not merely similar. The
/// operator <em>display name</em> is left <see langword="null"/> (design §8.1: no cross-database join) and
/// resolved by the handler through <see cref="IAnalyticsLabelReadStore"/>, the same way
/// <see cref="RollupOperatorAnalyticsReadStore"/> leaves it null for the app-layer merge.</para>
///
/// <para><b>Eventual consistency / the window-to-<c>local_day</c> mapping</b> is identical to
/// <see cref="RollupOperatorAnalyticsReadStore"/>'s - see that type's remarks; the UTC-date-over-local_day
/// bound is within `adr/0186`'s explicitly accepted staleness.</para>
/// </summary>
public sealed class RollupConversionReportReadStore(AnalyticsDbDataSource dataSource, AnalyticsOptions analyticsOptions)
    : IConversionReportReadStore
{
    // A grouped range scan on the rollup primary key, summing the outcome counts across the requested local
    // days for just the two dimensions this report needs. `sum(bigint)` returns `numeric` in Postgres; cast
    // back to bigint so it maps to the long columns of ConversionRollupRow.
    private const string ConversionReportSql = """
        select
            dimension_type                    as "DimensionType",
            dimension_key                     as "DimensionKey",
            sum(converted_count)::bigint      as "ConvertedCount",
            sum(not_converted_count)::bigint  as "NotConvertedCount",
            sum(follow_up_count)::bigint      as "FollowUpCount",
            sum(unset_count)::bigint          as "UnsetCount"
        from analytics_daily_rollups
        where site_id = @SiteId
          and local_day >= @FromDay
          and local_day <= @ToDay
          and dimension_type in (@TotalDimension, @OperatorDimension)
        group by dimension_type, dimension_key
        """;

    public async Task<ConversionReportResult> GetConversionReportAsync(
        SiteId siteId, DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken)
    {
        // Window bounds as calendar days at DateTimeKind.Unspecified so Npgsql binds them as `timestamp
        // without time zone` against the `date` column - see RollupOperatorAnalyticsReadStore for the full
        // reasoning; inclusive both ends, the identical mapping.
        var fromDay = DateTime.SpecifyKind(from.UtcDateTime.Date, DateTimeKind.Unspecified);
        var toDay = DateTime.SpecifyKind(to.UtcDateTime.Date, DateTimeKind.Unspecified);

        await using var connection = await dataSource.Value.OpenConnectionAsync(cancellationToken);

        var rows = (await connection.QueryAsync<ConversionRollupRow>(new CommandDefinition(
            ConversionReportSql,
            new
            {
                SiteId = siteId.Value,
                FromDay = fromDay,
                ToDay = toDay,
                TotalDimension = RollupDimensionTypes.Total,
                OperatorDimension = RollupDimensionTypes.Operator,
            },
            cancellationToken: cancellationToken))).ToList();

        // No total row means no rollup for any day in the window - an honest all-zero, null-rate bucket, the
        // same substitution the live store's BuildBucket makes over an empty row sequence.
        var overall = rows
            .Where(r => r.DimensionType == RollupDimensionTypes.Total)
            .Select(ToBucket)
            .FirstOrDefault() ?? new ConversionBucket(0, 0, 0, 0, 0, null);

        // Operator rows keyed by operator id as text; a non-Guid key is skipped defensively (never expected).
        // The ranking is the live store's exactly: threshold first, then rate, then recorded count, then the
        // operator id as the fully-deterministic final tie-break (no operator name is needed for the order).
        var byOperator = rows
            .Where(r => r.DimensionType == RollupDimensionTypes.Operator && Guid.TryParse(r.DimensionKey, out _))
            .Select(r => new ConversionOperatorBucket(new OperatorId(Guid.Parse(r.DimensionKey)), ToBucket(r)))
            .OrderByDescending(o => MeetsSampleThreshold(o.Bucket))
            .ThenByDescending(o => MeetsSampleThreshold(o.Bucket) ? o.Bucket.ConversionRate : null)
            .ThenByDescending(o => o.Bucket.RecordedCount)
            .ThenBy(o => o.Operator.Value)
            .ToList();

        return new ConversionReportResult(overall, byOperator);
    }

    private bool MeetsSampleThreshold(ConversionBucket bucket) =>
        bucket.RecordedCount >= analyticsOptions.MinimumSampleForRate;

    private static ConversionBucket ToBucket(ConversionRollupRow row)
    {
        // Recorded (the rate denominator) is converted + not-converted only - FollowUpNeeded and Unset are
        // excluded, the load-bearing `18-10` decision the live store and ConversionBucket both state.
        var recorded = row.ConvertedCount + row.NotConvertedCount;
        double? rate = recorded == 0 ? null : (double)row.ConvertedCount / recorded;
        return new ConversionBucket(
            row.ConvertedCount, row.NotConvertedCount, row.FollowUpCount, row.UnsetCount, recorded, rate);
    }
}
