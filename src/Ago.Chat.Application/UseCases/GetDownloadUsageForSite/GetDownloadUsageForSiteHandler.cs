using Ago.Chat.Application.Abstractions;
using Ago.Chat.Domain;
using Ago.Platform.Abstractions;
using Ago.Platform.Kernel;

namespace Ago.Chat.Application.UseCases.GetDownloadUsageForSite;

/// <summary>
/// `25-83`: the console banner's own data source - "crossing the soft threshold shows a
/// non-dismissable, warning-toned console banner" (`docs/backlog/25-83-*.md`'s own decision) needs
/// somewhere to read the current fact from, and this is it.
///
/// <para><b>Gated on <see cref="Permission.SiteConfigure"/></b> - the identical permission and
/// reasoning <see cref="Ago.Chat.Application.UseCases.GetSuspensionStatusForSite.GetSuspensionStatusForSiteHandler"/>'s
/// own remarks give for the same route group: "how is my own site set up" already covers "am I about
/// to be blocked."</para>
/// </summary>
public sealed class GetDownloadUsageForSiteHandler(
    ISiteRepository sites,
    IAttachmentEgressReadStore egressReads,
    IDownloadThresholdReadStore thresholds,
    IPermissionChecker permissions,
    IClock clock)
{
    public async Task<Result<DownloadUsageStatus>> HandleAsync(
        GetDownloadUsageForSite query, CancellationToken cancellationToken)
    {
        var allowed = await permissions.HasPermissionAsync(
            query.RequestedBy, query.SiteId, Permission.SiteConfigure, cancellationToken);
        if (!allowed)
        {
            return ConversationErrors.Forbidden("Operator does not have permission to view this site's download usage.");
        }

        var site = await sites.GetByIdAsync(query.SiteId, cancellationToken);
        if (site is null)
        {
            return ConversationErrors.SiteNotFound(query.SiteId.Value);
        }

        var now = clock.UtcNow;
        var periodMonth = new DateOnly(now.Year, now.Month, 1);
        var egress = await egressReads.GetForSiteAsync(query.SiteId, periodMonth, cancellationToken);
        var tierThresholds = await thresholds.GetForTierAsync(site.Tier, cancellationToken);

        var isSoftCrossed = egress.BytesOut >= tierThresholds.SoftThresholdBytes;
        var isHardCrossed = !site.DownloadBlockExempt && egress.BytesOut >= tierThresholds.HardThresholdBytes;

        return new DownloadUsageStatus(
            egress.BytesOut, tierThresholds.SoftThresholdBytes, tierThresholds.HardThresholdBytes,
            isSoftCrossed, isHardCrossed, site.DownloadBlockExempt);
    }
}
