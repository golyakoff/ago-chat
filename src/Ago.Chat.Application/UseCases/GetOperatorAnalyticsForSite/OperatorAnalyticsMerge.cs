using Ago.Chat.Application.Abstractions;
using Ago.Chat.Contracts;
using Ago.Chat.Domain;

namespace Ago.Chat.Application.UseCases.GetOperatorAnalyticsForSite;

/// <summary>
/// `23-18`: the per-operator merge <see cref="GetOperatorAnalyticsForSiteHandler"/> already performs
/// (union of "attributed to" and "held", `23-17`), extracted unchanged rather than duplicated so
/// `GetOwnAnalyticsForOperator.GetOwnAnalyticsForOperatorHandler` computes an operator's own row through
/// the identical code path the tenant's own report uses. This is the mechanism behind that item's own
/// central claim - "the operator's own figures equal their row in the tenant's report" - because
/// <c>internal static</c> means there is exactly one implementation of "how does load merge onto an
/// attributed bucket" in this assembly, not two that could quietly drift apart the day one of them
/// changes and the other is not remembered.
/// </summary>
internal static class OperatorAnalyticsMerge
{
    internal static readonly OperatorAnalyticsBucketDto ZeroBucketDto = new(0, null, null, 0);

    private static readonly OperatorAnalyticsBucket ZeroBucket = new(0, null, null, 0);

    /// <summary>The union of both operator sets, ordered ascending by <see cref="OperatorId.Value"/> -
    /// see <see cref="GetOperatorAnalyticsForSiteHandler"/>'s own remarks (now moved here) for why the
    /// union, not the intersection, and why an operator who never exceeded capacity still carries a
    /// real <c>0</c> rather than being silently absent.</summary>
    internal static IReadOnlyList<OperatorAnalyticsOperatorBucketDto> ComposeByOperator(
        OperatorAnalyticsResult result, IReadOnlyList<OperatorLoadSummary> loadSummaries)
    {
        var loadByOperator = loadSummaries.ToDictionary(l => l.Operator);
        var operatorIds = result.ByOperator.Select(o => o.Operator)
            .Concat(loadByOperator.Keys)
            .Distinct()
            .OrderBy(id => id.Value)
            .ToList();
        var byOperatorById = result.ByOperator.ToDictionary(o => o.Operator);

        return operatorIds.Select(operatorId =>
        {
            var attributed = byOperatorById.GetValueOrDefault(operatorId);
            var load = loadByOperator.GetValueOrDefault(operatorId);
            var operatorName = attributed?.OperatorName ?? load?.OperatorName;
            return new OperatorAnalyticsOperatorBucketDto(
                operatorId.Value,
                ToBucketDto(attributed?.Bucket ?? ZeroBucket),
                operatorName,
                load is null ? null : ToLoadDto(load));
        }).ToList();
    }

    internal static OperatorAnalyticsBucketDto ToBucketDto(OperatorAnalyticsBucket bucket) => new(
        bucket.ConversationCount, bucket.AverageFirstResponseSeconds, bucket.AverageDurationSeconds, bucket.MissedCount);

    internal static OperatorLoadSummaryDto ToLoadDto(OperatorLoadSummary summary) => new(
        summary.ConversationsHeld,
        summary.IntervalsHeld,
        summary.StandardIntervals,
        summary.AdditionalIntervals,
        summary.ByLoad
            .Select(b => new OperatorLoadBucketEntryDto(b.BucketLabel, b.IntervalCount, b.ReplyCount, b.AverageFirstReplySeconds))
            .ToList());
}
