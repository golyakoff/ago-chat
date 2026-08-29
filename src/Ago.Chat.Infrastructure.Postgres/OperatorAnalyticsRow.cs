namespace Ago.Chat.Infrastructure.Postgres;

/// <summary>`18-08`: the flat row <see cref="OperatorAnalyticsReadStore"/>'s SQL materializes, one per
/// grouping-set output - <see cref="Channel"/> <see langword="null"/> is the site-wide total row
/// (Postgres's own `GROUPING SETS ((), (channel_label))` behaviour), a real value is one channel's
/// bucket. No <see cref="DateTime"/>/<see cref="DateTimeOffset"/> field here, unlike
/// <see cref="SiteOverviewRow"/>/<see cref="ConversationSummaryRow"/> - every timestamp this query
/// touches is reduced to a count or an interval inside the SQL itself, so there is no provider-offset
/// conversion left to do at this boundary.</summary>
internal sealed record OperatorAnalyticsRow(
    string? Channel, long ConversationCount, long MissedCount, double? AverageFirstResponseSeconds);
