namespace Ago.Chat.Infrastructure.Postgres;

/// <summary>`23-17`: one operator's totals row - see <see cref="OperatorLoadReportReadStore"/> for the
/// full "why this shape" statement, including why totals and per-load-bucket numbers are two separate
/// queries rather than one `GROUPING SETS` query the way <see cref="OperatorAnalyticsReadStore"/>'s own
/// sibling report computes its several dimensions.</summary>
internal sealed record OperatorLoadTotalsRow(
    Guid OperatorId,
    string? OperatorName,
    long IntervalCount,
    long ConversationCount,
    long AdditionalCount);

/// <summary>`23-17`: one operator's rows at one exact concurrent-load value - folded into
/// <see cref="Application.Abstractions.OperatorLoadBuckets"/>'s own configured buckets by
/// <see cref="OperatorLoadReportReadStore"/>, in C#, not in this query's own `GROUP BY`.
/// <see cref="ConcurrentLoad"/> is <c>long</c>, not <c>int</c> - it is `count(*)` over a correlated
/// subquery, and Postgres's own `count(*)` is always `bigint` regardless of how small the actual count
/// gets; found live, the same "match the reader's own column type, not whatever comfortably holds the
/// values" lesson recorded on <see cref="OperatorLoadTotalsRow"/>'s own sibling file history.</summary>
internal sealed record OperatorLoadBucketRow(
    Guid OperatorId,
    long ConcurrentLoad,
    long IntervalCount,
    long ReplyCount,
    double ReplySecondsSum);
