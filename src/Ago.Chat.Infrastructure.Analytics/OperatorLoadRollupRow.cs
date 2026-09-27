namespace Ago.Chat.Infrastructure.Analytics;

/// <summary>`26-237`/`adr/0186` (operator-load rollup, decision B): one grouped row read from
/// <c>analytics_operator_load_rollups</c> in the <c>ago_analytics</c> database - one operator at one
/// exact concurrent load, summed across the requested local days. <see cref="RollupOperatorLoadReportReadStore"/>
/// folds however many exact-load rows an operator has into
/// <c>AnalyticsOptions.LoadBucketUpperBounds</c>'s configured buckets in C# (the bucketing is AGO Chat
/// configuration, kept out of the ago-analytics writer). The counters are <c>sum(bigint)::bigint</c> in
/// the query, so they map to <see langword="long"/>; <see cref="ConcurrentLoad"/> is the group key, an
/// <see cref="int"/>.</summary>
internal sealed record OperatorLoadRollupRow(
    Guid OperatorId,
    int ConcurrentLoad,
    long IntervalCount,
    long AdditionalIntervalCount,
    long DistinctConversationCount,
    long ReplyCount,
    long ReplySecondsSum);
