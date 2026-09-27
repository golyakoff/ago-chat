using Ago.Chat.Application.Abstractions;
using Ago.Chat.Domain;
using Dapper;

namespace Ago.Chat.Infrastructure.Analytics;

/// <summary>
/// `26-223`/`adr/0186` (`docs/design/analytics-precompute.md` §8): the precomputed-rollup implementation of
/// <see cref="IOperatorAnalyticsReadStore"/>. It answers the identical port question the live
/// <c>Ago.Chat.Infrastructure.Postgres.OperatorAnalyticsReadStore</c> does - and returns the identical
/// <see cref="OperatorAnalyticsResult"/> shape - but instead of aggregating O(conversations) live over the
/// operational <c>ago_chat</c> Postgres on every request (a per-conversation <c>LATERAL</c> + <c>GROUPING
/// SETS</c> scan, the shape that produced the "аналитика отъехала" 499), it reads a grouped range scan over
/// the narrow <c>analytics_daily_rollups</c> table in the dedicated <c>ago_analytics</c> database: O(days),
/// independent of total history, and it never touches <c>messages</c> or <c>ago_chat</c> at all.
///
/// <para><b>Why this adapter reads a <em>second</em> database via Dapper (teaching-mode).</b> Analytics
/// reads are cross-store but strictly read-only and issue no join back to <c>ago_chat</c> (`adr/0186` §3.2,
/// §8.1). The dependency rule is unbroken: the port <see cref="IOperatorAnalyticsReadStore"/> lives in
/// Application and knows neither which database backs it nor that there are now two candidates; only this
/// Infrastructure adapter knows it reads <c>ago_analytics</c> through <see cref="AnalyticsDbDataSource"/>.
/// The alternative - a cross-database <c>JOIN</c> to resolve labels, or reading the rollups from inside
/// <c>ago_chat</c> - was rejected by the design because it would weld the two databases to one host and
/// defeat the whole point (moving analytics load off the operational bottleneck, and keeping the rollups
/// independently relocatable). Labels that are intrinsic (channel, referrer host, campaign) are denormalized
/// onto the rollup row; the one label that is an id (operator) is resolved in the application layer
/// (<c>OperatorAnalyticsMerge</c> already merges operator display names from the live load report), never
/// here.
///
/// <para><b>Averages are reconstituted from stored sums and counts, never stored as ratios</b> (design §7):
/// the rollup keeps <c>first_response_seconds_sum</c> / <c>first_response_count</c> (and the duration pair)
/// so the numbers stay additive across days, and this read divides them back - <see langword="null"/> when
/// the denominator is zero, the identical "nothing to average yet, never zero, never a sentinel" rule the
/// port documents.</para>
///
/// <para><b>Eventual consistency, and how the window maps to <c>local_day</c>.</b> The rollups are keyed by
/// the <em>tenant-local calendar day</em> (design §9); the port's window arrives as UTC instants. The port
/// signature is deliberately unchanged and carries no tenant zone, so this read maps the instant window to
/// an inclusive UTC-date range over <c>local_day</c>. Because the default tenant zone is <c>Europe/Moscow</c>
/// (UTC+3, no DST) the boundary can drift by at most a few hours' worth of conversations at each end - well
/// within `adr/0186`'s explicitly accepted staleness (the report data may already lag ~1h under the hourly
/// aggregator; a fast read with a shown freshness marker is the contract, not query-time exactness). Exact
/// tenant-zone window alignment would require the site zone in the read path, which the unchanged port does
/// not carry - a deliberate trade, not an oversight.</para>
/// </summary>
public sealed class RollupOperatorAnalyticsReadStore(AnalyticsDbDataSource dataSource) : IOperatorAnalyticsReadStore
{
    // Named constants rather than string literals in the SQL, the same discipline the operational store
    // applies to its own enum-member strings - a dimension-type typo is then a compile error here, not a
    // query that silently matches nothing.
    private static readonly string TotalDimension = RollupDimensionTypes.Total;
    private static readonly string ChannelDimension = RollupDimensionTypes.Channel;
    private static readonly string OperatorDimension = RollupDimensionTypes.Operator;
    private static readonly string ReferrerDimension = RollupDimensionTypes.Referrer;
    private static readonly string CampaignDimension = RollupDimensionTypes.Campaign;

    // A grouped range scan on the rollup primary key `(site_id, local_day, dimension_type, dimension_key)`,
    // summing each metric across the requested local days. O(days), never O(conversations); no join to any
    // other table and none to `ago_chat`.
    private const string SiteAnalyticsSql = """
        select
            dimension_type                        as "DimensionType",
            dimension_key                         as "DimensionKey",
            -- `sum(bigint)` returns `numeric` in Postgres; cast back to bigint so it maps to the long
            -- columns of AnalyticsRollupRow (the counters cannot overflow bigint at this volume).
            sum(conversation_count)::bigint       as "ConversationCount",
            sum(missed_count)::bigint             as "MissedCount",
            sum(first_response_seconds_sum)::bigint as "FirstResponseSecondsSum",
            sum(first_response_count)::bigint     as "FirstResponseCount",
            sum(duration_seconds_sum)::bigint     as "DurationSecondsSum",
            sum(duration_count)::bigint           as "DurationCount"
        from analytics_daily_rollups
        where site_id = @SiteId
          and local_day >= @FromDay
          and local_day <= @ToDay
        group by dimension_type, dimension_key
        """;

    public async Task<OperatorAnalyticsResult> GetSiteAnalyticsAsync(
        SiteId siteId, DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken)
    {
        // The window bounds as calendar days (midnight), passed as DateTimeKind.Unspecified so Npgsql binds
        // them as `timestamp without time zone`, not `timestamptz` - the comparison against the `date`
        // column is then TZ-independent (Postgres promotes the date to a midnight timestamp), never shifted
        // by the connection's session time zone. `DateOnly` is deliberately not used: Dapper's parameter
        // binder in this version does not accept it. Inclusive both ends - see the class remarks on the
        // UTC-date-over-local_day mapping and why it is within adr/0186's accepted staleness.
        var fromDay = DateTime.SpecifyKind(from.UtcDateTime.Date, DateTimeKind.Unspecified);
        var toDay = DateTime.SpecifyKind(to.UtcDateTime.Date, DateTimeKind.Unspecified);

        await using var connection = await dataSource.Value.OpenConnectionAsync(cancellationToken);

        var rows = (await connection.QueryAsync<AnalyticsRollupRow>(new CommandDefinition(
            SiteAnalyticsSql,
            new { SiteId = siteId.Value, FromDay = fromDay, ToDay = toDay },
            cancellationToken: cancellationToken))).ToList();

        // No row at all means no rollup for any day in the window - the honest answer is an explicit zero
        // bucket, not an empty response the caller would have to special-case (the same substitution the
        // live store makes for a window with zero conversations).
        var overall = rows
            .Where(r => r.DimensionType == TotalDimension)
            .Select(ToBucket)
            .FirstOrDefault() ?? new OperatorAnalyticsBucket(0, null, null, 0);

        var byChannel = rows
            .Where(r => r.DimensionType == ChannelDimension)
            .Select(r => new OperatorAnalyticsChannelBucket(r.DimensionKey, ToBucket(r)))
            .OrderBy(c => c.Channel, StringComparer.Ordinal)
            .ToList();

        // `dimension_key` is the operator id as text; the display name is resolved in the application layer
        // (design §8.1), so OperatorName is left null here. A row whose key is not a parseable Guid is
        // skipped rather than trusted - defensive against a malformed rollup, never expected in practice.
        var byOperator = rows
            .Where(r => r.DimensionType == OperatorDimension && Guid.TryParse(r.DimensionKey, out _))
            .Select(r => new OperatorAnalyticsOperatorBucket(new OperatorId(Guid.Parse(r.DimensionKey)), ToBucket(r)))
            .OrderBy(o => o.Operator.Value)
            .ToList();

        var byReferrer = rows
            .Where(r => r.DimensionType == ReferrerDimension)
            .Select(r => new OperatorAnalyticsReferrerBucket(r.DimensionKey, ToBucket(r)))
            .OrderBy(r => r.ReferrerHost, StringComparer.Ordinal)
            .ToList();

        var byCampaign = rows
            .Where(r => r.DimensionType == CampaignDimension)
            .Select(r => new OperatorAnalyticsCampaignBucket(r.DimensionKey, ToBucket(r)))
            .OrderBy(c => c.UtmCampaign, StringComparer.Ordinal)
            .ToList();

        return new OperatorAnalyticsResult(overall, byChannel, byOperator, byReferrer, byCampaign);
    }

    private static OperatorAnalyticsBucket ToBucket(AnalyticsRollupRow row) => new(
        row.ConversationCount,
        row.FirstResponseCount > 0 ? (double)row.FirstResponseSecondsSum / row.FirstResponseCount : null,
        row.DurationCount > 0 ? (double)row.DurationSecondsSum / row.DurationCount : null,
        row.MissedCount);
}
