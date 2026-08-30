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
    IConversionReportReadStore readStore, IPermissionChecker permissions, IClock clock)
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

        var result = await readStore.GetConversionReportAsync(query.SiteId, from, to, cancellationToken);

        return new ConversionReportResponse(
            from,
            to,
            ToDto(result.Overall),
            result.ByOperator.Select(o => new ConversionOperatorBucketDto(o.Operator.Value, ToDto(o.Bucket))).ToList());
    }

    private static ConversionBucketDto ToDto(ConversionBucket bucket) => new(
        bucket.ConvertedCount, bucket.NotConvertedCount, bucket.FollowUpNeededCount, bucket.UnsetCount,
        bucket.RecordedCount, bucket.ConversionRate);
}
