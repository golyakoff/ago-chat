namespace Ago.Chat.Infrastructure.Postgres;

/// <summary>`18-08`: the flat row <see cref="OperatorAnalyticsReadStore"/>'s SQL materializes, one per
/// grouping-set output - <see cref="Channel"/> <see langword="null"/> is the site-wide total row
/// (Postgres's own `GROUPING SETS ((), (channel_label), (attributed_operator_id))` behaviour), a real
/// value is one channel's bucket. No <see cref="DateTime"/>/<see cref="DateTimeOffset"/> field here,
/// unlike <see cref="SiteOverviewRow"/>/<see cref="ConversationSummaryRow"/> - every timestamp this
/// query touches is reduced to a count or an interval inside the SQL itself, so there is no
/// provider-offset conversion left to do at this boundary.
///
/// <para>`18-09`: <see cref="OperatorId"/> and <see cref="OperatorGrouping"/> are the per-operator
/// grouping set's own columns - see <see cref="OperatorAnalyticsReadStore"/>'s class remarks for why a
/// third grouping-set column needs its own `grouping()` flag (unlike <see cref="Channel"/>, whose "not
/// in this grouping set" and "no channel identity at all" cases were already distinguishable - the
/// `Widget` fallback label is never `NULL`) to tell the site-wide total row apart from the per-operator
/// set's own "nobody was ever assigned" bucket, which shares the same `NULL`/`NULL` output otherwise.
/// </para>
///
/// <para>`18-13`: <see cref="AverageDurationSeconds"/> is one more nullable `avg(...)` column, mapped
/// by name the same way <see cref="AverageFirstResponseSeconds"/> already is - Dapper matches a
/// record's constructor parameters against the SQL's own column aliases by name, not position, so this
/// property's place in the parameter list does not need to match the `select` list's.</para>
///
/// <para>`18-12`: <see cref="ReferrerHost"/>/<see cref="UtmCampaign"/> are the two new grouping sets'
/// own columns, and <see cref="ChannelGrouping"/>/<see cref="ReferrerGrouping"/>/
/// <see cref="CampaignGrouping"/> are their `grouping()` flags - see
/// <see cref="OperatorAnalyticsReadStore"/>'s class remarks for why every dimension needs its own flag
/// now that there are five grouping sets rather than three.</para>
///
/// <para>`23-02`: <see cref="OperatorName"/> is placed right after <see cref="OperatorId"/>, matching
/// `SiteAnalyticsSql`'s own `select` order - found live, correcting this file's own paragraph above:
/// a record parameter with a default value (`= null`) breaks Dapper's constructor selection when it
/// does not also sit at the position its column occupies in the reader (`OperatorAnalyticsReadStoreTests`
/// caught it - every test calling `GetSiteAnalyticsAsync` failed with "no matching signature" until
/// this was reordered). <see cref="AverageDurationSeconds"/> above has no default value and genuinely
/// does bind by name regardless of position, which is why that paragraph's claim was never wrong for
/// every column but this one.</para></summary>
internal sealed record OperatorAnalyticsRow(
    string? Channel,
    Guid? OperatorId,
    string? OperatorName,
    string? ReferrerHost,
    string? UtmCampaign,
    long ConversationCount,
    long MissedCount,
    double? AverageFirstResponseSeconds,
    double? AverageDurationSeconds,
    int ChannelGrouping,
    int OperatorGrouping,
    int ReferrerGrouping,
    int CampaignGrouping);
