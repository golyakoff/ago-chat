using Ago.Chat.Application.Abstractions;
using Ago.Chat.Application.UseCases.GetOperatorAnalyticsForSite;
using Ago.Chat.Contracts;
using Ago.Chat.Domain;
using Ago.Platform.Kernel;

namespace Ago.Chat.Application.UseCases.GetOwnAnalyticsForOperator;

/// <summary>
/// `23-18`: no <see cref="IPermissionChecker"/> dependency, deliberately - the same reasoning
/// <c>ConversationsEndpoints</c> states again where this route is mapped. An operator reading their own
/// row is not a grant a tenant could withhold; the backlog item's own words are "a grant would be a
/// thing a tenant could withhold - which is the failure this item exists to prevent." The only
/// authorization question this handler has is "is this a real operator of this site", which
/// `RequireOperatorIdentity` already answers before this handler ever runs - the same policy every
/// other conversation route in this codebase already requires
/// (<c>GetOperatorAnalyticsForSiteHandler</c>'s own sibling remarks name the one route in this file's
/// group that is gated further, and this is deliberately not that route).
///
/// <para><b>Not a second computation.</b> This calls <see cref="IOperatorAnalyticsReadStore"/>,
/// <see cref="IOperatorLoadReportReadStore"/> and <see cref="IConversionReportReadStore"/> exactly as
/// <c>GetOperatorAnalyticsForSiteHandler</c>/<c>GetConversionReportForSiteHandler</c> already do - same
/// window, same default, same merge (<see cref="OperatorAnalyticsMerge.ComposeByOperator"/>, extracted
/// from that handler for this reason) - then keeps only the row addressed to the caller's own
/// <see cref="OperatorId"/>. Sharing the merge rather than restating it is what makes "the operator's own
/// figures equal their row in the tenant's report" true by construction: there is exactly one piece of
/// code that decides what an operator's row looks like, and both handlers call it.</para>
///
/// <para><b>Own page, never a 404.</b> An operator who held nothing and converted nothing in the window
/// still gets a response - a zero-filled <see cref="OwnOperatorAnalyticsResponse.Bucket"/>, and
/// <see langword="null"/> for <see cref="OwnOperatorAnalyticsResponse.Load"/>/
/// <see cref="OwnOperatorAnalyticsResponse.Conversion"/>, the identical "a real zero is a fact, a
/// missing row is a different fact" distinction <c>OperatorLoadSummaryDto</c>'s own remarks already
/// draw for the tenant's report. The alternative - an empty/`404` response for an idle window - would
/// read as an error on a screen that is supposed to be boring on a slow day, not broken.</para>
/// </summary>
public sealed class GetOwnAnalyticsForOperatorHandler(
    IOperatorAnalyticsReadStore readStore,
    IOperatorLoadReportReadStore loadReportReadStore,
    IConversionReportReadStore conversionReadStore,
    IClock clock)
{
    /// <summary>Restated rather than referenced against
    /// <see cref="GetOperatorAnalyticsForSiteHandler.DefaultWindowDays"/> - that handler's own remarks
    /// explain why (`Ago.Chat.Application` has no cross-use-case constant for this), and this screen's
    /// default is a UX default for a different screen, not a fact that must stay numerically identical
    /// forever.</summary>
    public const int DefaultWindowDays = 30;

    public async Task<Result<OwnOperatorAnalyticsResponse>> HandleAsync(
        GetOwnAnalyticsForOperator query, CancellationToken cancellationToken)
    {
        var to = query.To ?? clock.UtcNow;
        var from = query.From ?? to.AddDays(-DefaultWindowDays);
        if (from >= to)
        {
            return ConversationErrors.AnalyticsInvalidRange("The report range's start must be before its end.");
        }

        // The same site-wide reads the tenant's own reports issue, called with the caller's own site -
        // never a caller-supplied one, since `GetOwnAnalyticsForOperator` carries no other id to read it
        // from. All three run concurrently, the same `Task.WhenAll` shape
        // `GetOperatorAnalyticsForSiteHandler` already established for its own three-way call.
        var analyticsTask = readStore.GetSiteAnalyticsAsync(query.SiteId, from, to, cancellationToken);
        var loadTask = loadReportReadStore.GetOperatorLoadReportAsync(query.SiteId, from, to, cancellationToken);
        var conversionTask = conversionReadStore.GetConversionReportAsync(query.SiteId, from, to, cancellationToken);
        await Task.WhenAll(analyticsTask, loadTask, conversionTask);

        var byOperator = OperatorAnalyticsMerge.ComposeByOperator(analyticsTask.Result, loadTask.Result);
        var ownRow = byOperator.SingleOrDefault(o => o.OperatorId == query.RequestedBy.Value);
        var ownConversionRow = conversionTask.Result.ByOperator.SingleOrDefault(o => o.Operator == query.RequestedBy);

        return new OwnOperatorAnalyticsResponse(
            from,
            to,
            ownRow?.Bucket ?? OperatorAnalyticsMerge.ZeroBucketDto,
            ownRow?.Load,
            ownConversionRow is null ? null : ToConversionDto(ownConversionRow.Bucket));
    }

    private static ConversionBucketDto ToConversionDto(ConversionBucket bucket) => new(
        bucket.ConvertedCount, bucket.NotConvertedCount, bucket.FollowUpNeededCount, bucket.UnsetCount,
        bucket.RecordedCount, bucket.ConversionRate);
}
