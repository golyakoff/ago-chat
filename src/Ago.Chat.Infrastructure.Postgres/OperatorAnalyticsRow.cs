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
/// property's place in the parameter list does not need to match the `select` list's.</para></summary>
internal sealed record OperatorAnalyticsRow(
    string? Channel,
    Guid? OperatorId,
    long ConversationCount,
    long MissedCount,
    double? AverageFirstResponseSeconds,
    double? AverageDurationSeconds,
    int OperatorGrouping);
