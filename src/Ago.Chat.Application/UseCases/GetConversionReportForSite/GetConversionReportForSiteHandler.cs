using Ago.Chat.Application.Abstractions;
using Ago.Chat.Contracts;
using Ago.Chat.Domain;
using Ago.Platform.Kernel;

namespace Ago.Chat.Application.UseCases.GetConversionReportForSite;

/// <summary>
/// `18-10`: the report half of this item. `18-08`'s <c>GetOperatorAnalyticsForSiteHandler</c> is the
/// direct precedent for every structural choice below - same default window, same range validation, same
/// pass-through-and-shape-the-wire-response split with its own read store.
///
/// <para><b>Gated on <see cref="Permission.SiteConfigure"/></b>, not <see cref="Permission.ConversationRead"/>
/// - the identical reasoning `GetOperatorAnalyticsForSiteHandler`'s own remarks give: this report is
/// computed over every conversation on the site, including every other operator's, which is the
/// site-wide oversight boundary `authorization.md`'s admin/supervisor role exists to draw, not something
/// the ordinary per-operator `conversation:read` grant should unlock.</para>
/// </summary>
public sealed class GetConversionReportForSiteHandler(
    IConversionReportReadStore readStore,
    IAnalyticsLabelReadStore labelReadStore,
    IPermissionChecker permissions,
    IClock clock)
{
    /// <summary>Restated rather than referenced against `GetOperatorAnalyticsForSiteHandler.DefaultWindowDays`
    /// - that handler's own remarks explain why (`Ago.Chat.Application` has no cross-use-case constant
    /// for this), and this report's default is a UX default for a different report, not a fact that must
    /// stay numerically identical to that one forever.</summary>
    public const int DefaultWindowDays = 30;

    public async Task<Result<ConversionReportResponse>> HandleAsync(
        GetConversionReportForSite query, CancellationToken cancellationToken)
    {
        var allowed = await permissions.HasPermissionAsync(
            query.RequestedBy, query.SiteId, Permission.SiteConfigure, cancellationToken);
        if (!allowed)
        {
            return ConversationErrors.Forbidden("Operator does not have permission to view this site's conversion report.");
        }

        var to = query.To ?? clock.UtcNow;
        var from = query.From ?? to.AddDays(-DefaultWindowDays);
        if (from >= to)
        {
            return ConversationErrors.AnalyticsInvalidRange("The report range's start must be before its end.");
        }

        // `23-16`: the preceding window is read through the identical single-window port, called a
        // second time, rather than a second read-store method - `PrecedingPeriod`'s own remarks on why
        // this stays application-layer orchestration instead of new SQL. Both calls are issued before
        // either is awaited, so they run concurrently rather than doubling this handler's own latency.
        var (previousFrom, previousTo) = PrecedingPeriod.Before(from, to);
        var currentTask = readStore.GetConversionReportAsync(query.SiteId, from, to, cancellationToken);
        var previousTask = readStore.GetConversionReportAsync(query.SiteId, previousFrom, previousTo, cancellationToken);
        await Task.WhenAll(currentTask, previousTask);
        var result = currentTask.Result;
        var previousResult = previousTask.Result;

        // `26-237`/`adr/0186` §8.1: resolve any operator whose name the read store did not carry. The live
        // ago_chat store already joins the name in, so nothing is unresolved and this touches no database;
        // the rollup store returns id-keyed rows with a null name, so this is where those names come from -
        // an application-layer merge across two read ports, never a cross-database SQL join. Only the current
        // window's per-operator rows carry names (the preceding window contributes only the overall bucket).
        var names = await ResolveOperatorNamesAsync(query.SiteId, result.ByOperator, cancellationToken);

        return new ConversionReportResponse(
            from,
            to,
            ToDto(result.Overall),
            previousFrom,
            previousTo,
            ToDto(previousResult.Overall),
            result.ByOperator
                .Select(o => new ConversionOperatorBucketDto(
                    o.Operator.Value, ToDto(o.Bucket), o.OperatorName ?? names.GetValueOrDefault(o.Operator)))
                .ToList());
    }

    private async Task<IReadOnlyDictionary<OperatorId, string>> ResolveOperatorNamesAsync(
        SiteId siteId, IReadOnlyList<ConversionOperatorBucket> byOperator, CancellationToken cancellationToken)
    {
        var unresolved = byOperator.Where(o => o.OperatorName is null).Select(o => o.Operator).ToList();
        return unresolved.Count == 0
            ? EmptyNames
            : await labelReadStore.GetOperatorDisplayNamesAsync(siteId, unresolved, cancellationToken);
    }

    private static readonly IReadOnlyDictionary<OperatorId, string> EmptyNames = new Dictionary<OperatorId, string>();

    private static ConversionBucketDto ToDto(ConversionBucket bucket) => new(
        bucket.ConvertedCount, bucket.NotConvertedCount, bucket.FollowUpNeededCount, bucket.UnsetCount,
        bucket.RecordedCount, bucket.ConversionRate);
}
