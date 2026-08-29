using Ago.Chat.Application.Abstractions;
using Ago.Chat.Contracts;
using Ago.Chat.Domain;
using Ago.Platform.Kernel;

namespace Ago.Chat.Application.UseCases.GetOperatorAnalyticsForSite;

/// <summary>
/// `18-08`: the console's own basic self-service report - conversation volume, average first-response
/// time and missed-conversation count, per channel and overall, for one site over one window.
///
/// <para><b>Gated on <see cref="Permission.SiteConfigure"/>, not <see cref="Permission.ConversationRead"/>
/// - the same call `GetAllConversationsForSiteHandler` and `18-01`'s <c>SearchConversationsHandler</c>
/// already make, for the identical reason.</b> This report is computed over every conversation on the
/// site, not the caller's own assigned/waiting ones - <see cref="Permission.ConversationRead"/> is what
/// every ordinary operator already holds and only ever unlocks their own queue view
/// (<c>GetOperatorQueueHandler</c>); extending it here would let any operator read the whole site's
/// performance numbers, including how other operators are doing, which is exactly the site-wide
/// oversight boundary `authorization.md`'s admin/supervisor role exists to draw. The backlog item's own
/// "Open questions" left this choice open; `site:configure` is the answer, on this precedent.</para>
/// </summary>
public sealed class GetOperatorAnalyticsForSiteHandler(
    IOperatorAnalyticsReadStore readStore, IPermissionChecker permissions, IClock clock)
{
    /// <summary>Thirty days, the same width `12-02`'s <c>ListSitesForOwnerHandler.RecentWindowDays</c>
    /// already uses for an analogous "no range named" default - restated here rather than referenced
    /// (`Ago.Chat.Application` has no cross-use-case constant for it), and, like that one, an
    /// operational default rather than a measurement (`CLAUDE.md`'s ban on invented figures governs
    /// "how slow is too slow", not "how wide a window to default to").</summary>
    public const int DefaultWindowDays = 30;

    public async Task<Result<OperatorAnalyticsResponse>> HandleAsync(
        GetOperatorAnalyticsForSite query, CancellationToken cancellationToken)
    {
        var allowed = await permissions.HasPermissionAsync(
            query.RequestedBy, query.SiteId, Permission.SiteConfigure, cancellationToken);
        if (!allowed)
        {
            return ConversationErrors.Forbidden("Operator does not have permission to view this site's analytics.");
        }

        var to = query.To ?? clock.UtcNow;
        var from = query.From ?? to.AddDays(-DefaultWindowDays);
        if (from >= to)
        {
            return ConversationErrors.AnalyticsInvalidRange("The report range's start must be before its end.");
        }

        var result = await readStore.GetSiteAnalyticsAsync(query.SiteId, from, to, cancellationToken);

        return new OperatorAnalyticsResponse(
            from,
            to,
            ToDto(result.Overall),
            result.ByChannel.Select(c => new OperatorAnalyticsChannelBucketDto(c.Channel, ToDto(c.Bucket))).ToList());
    }

    private static OperatorAnalyticsBucketDto ToDto(OperatorAnalyticsBucket bucket) => new(
        bucket.ConversationCount, bucket.AverageFirstResponseSeconds, bucket.MissedCount);
}
