using Ago.Chat.Application.Abstractions;
using Ago.Chat.Contracts;
using Ago.Chat.Domain;
using Ago.Platform.Kernel;

namespace Ago.Chat.Application.UseCases.GetTagBreakdownReportForSite;

/// <summary>
/// `18-11`: the tag-breakdown half of this item. `GetConversionReportForSiteHandler` is the direct
/// precedent for every structural choice below - same default window, same range validation, same
/// pass-through-and-shape-the-wire-response split with its own read store; every real decision (what
/// "tagged" means, why a multi-tag conversation counts once per tag, why the percentage-tagged figure
/// exists) lives at <see cref="ITagBreakdownReadStore"/>, not here.
///
/// <para><b>Gated on <see cref="Permission.SiteConfigure"/></b>, not <see cref="Permission.ConversationRead"/>
/// - the identical reasoning `GetOperatorAnalyticsForSiteHandler`/`GetConversionReportForSiteHandler`'s
/// own remarks give: this report is computed over every conversation on the site, including every other
/// operator's, which is the site-wide oversight boundary `authorization.md`'s admin/supervisor role
/// exists to draw, not something the ordinary per-operator `conversation:read` grant should unlock.</para>
/// </summary>
public sealed class GetTagBreakdownReportForSiteHandler(
    ITagBreakdownReadStore readStore,
    IAnalyticsLabelReadStore labelReadStore,
    IPermissionChecker permissions,
    IClock clock)
{
    /// <summary>Restated rather than referenced against the sibling reports' own constants - the same
    /// "`Ago.Chat.Application` has no cross-use-case constant for this" reasoning
    /// `GetConversionReportForSiteHandler.DefaultWindowDays`'s own remarks already give.</summary>
    public const int DefaultWindowDays = 30;

    public async Task<Result<TagBreakdownReportResponse>> HandleAsync(
        GetTagBreakdownReportForSite query, CancellationToken cancellationToken)
    {
        var allowed = await permissions.HasPermissionAsync(
            query.RequestedBy, query.SiteId, Permission.SiteConfigure, cancellationToken);
        if (!allowed)
        {
            return ConversationErrors.Forbidden("Operator does not have permission to view this site's tag breakdown report.");
        }

        var to = query.To ?? clock.UtcNow;
        var from = query.From ?? to.AddDays(-DefaultWindowDays);
        if (from >= to)
        {
            return ConversationErrors.AnalyticsInvalidRange("The report range's start must be before its end.");
        }

        // `23-16`: same shape `GetConversionReportForSiteHandler` establishes - the preceding window
        // read through the identical single-window port, called a second time, both calls issued
        // before either is awaited.
        var (previousFrom, previousTo) = PrecedingPeriod.Before(from, to);
        var currentTask = readStore.GetTagBreakdownAsync(query.SiteId, from, to, cancellationToken);
        var previousTask = readStore.GetTagBreakdownAsync(query.SiteId, previousFrom, previousTo, cancellationToken);
        await Task.WhenAll(currentTask, previousTask);
        var result = currentTask.Result;
        var previousResult = previousTask.Result;

        // `26-237`/`adr/0186` §8.1: fill any tag whose name the read store did not carry. The live ago_chat
        // store joins the name in (nothing unresolved, no query); the rollup store returns id-keyed rows with
        // an empty placeholder name, resolved here through the application-layer label read - never a
        // cross-database join. Only the current window's tag rows are shown, so only they need names.
        var names = await ResolveTagNamesAsync(query.SiteId, result.ByTag, cancellationToken);

        return new TagBreakdownReportResponse(
            from,
            to,
            result.TotalConversationCount,
            result.TaggedConversationCount,
            result.PercentageTagged,
            previousFrom,
            previousTo,
            previousResult.TotalConversationCount,
            previousResult.TaggedConversationCount,
            previousResult.PercentageTagged,
            result.ByTag.Select(b => ToDto(b, names)).ToList());
    }

    private async Task<IReadOnlyDictionary<TagId, string>> ResolveTagNamesAsync(
        SiteId siteId, IReadOnlyList<TagBreakdownBucket> byTag, CancellationToken cancellationToken)
    {
        var unresolved = byTag.Where(b => string.IsNullOrEmpty(b.TagName)).Select(b => b.TagId).ToList();
        return unresolved.Count == 0
            ? EmptyNames
            : await labelReadStore.GetTagNamesAsync(siteId, unresolved, cancellationToken);
    }

    private static readonly IReadOnlyDictionary<TagId, string> EmptyNames = new Dictionary<TagId, string>();

    private static TagBreakdownBucketDto ToDto(
        TagBreakdownBucket bucket, IReadOnlyDictionary<TagId, string> resolvedNames) => new(
        bucket.TagId.Value,
        string.IsNullOrEmpty(bucket.TagName) ? resolvedNames.GetValueOrDefault(bucket.TagId, string.Empty) : bucket.TagName,
        bucket.ConversationCount,
        bucket.ConvertedCount,
        bucket.NotConvertedCount,
        bucket.RecordedCount,
        bucket.ConversionRate);
}
