using Ago.Chat.Application.Abstractions;
using Ago.Chat.Domain;
using Ago.Platform.Kernel;

namespace Ago.Chat.Application.UseCases.GetSiteInstallation;

/// <summary>
/// `10-06`: closes the gap the backlog item states plainly - `Ago.Chat.Api` had a visitor-facing
/// endpoint that *consumes* a site's public key (`POST /api/v1/visitor-sessions`) and nothing that
/// *returns* one to the operator who owns the site it identifies. Modeled on
/// <see cref="GetWidgetConfig.GetWidgetConfigHandler"/> byte-for-byte: an operator-authenticated,
/// low-frequency admin read, not wrapped in `ICache.GetOrCreateAsync` for the identical reason that
/// handler's own remarks give (this is the console's own installation screen checking what the site is
/// currently configured with, not something every visitor's page load hits).
///
/// <para><b>`23-06`: two more reads join the original two.</b> <see cref="ISiteInstallationSignalRepository.GetAsync"/>
/// answers "was the widget seen" and <see cref="IConversationReadStore.GetMostRecentCreatedAtAsync"/>
/// answers "was the product used" - both go straight to Postgres, uncached, for the identical reason
/// `caching.md` gives for every other admin read in this handler: a stale answer on the one screen that
/// exists to tell a tenant the truth about their own install would be the exact defect this item
/// closes elsewhere reappearing here. <see cref="SiteInstallationStateResolver.Resolve"/> is then the
/// one place both facts are folded into a single <see cref="Domain.SiteInstallationState"/> - pure
/// Domain logic, no I/O of its own, called from here rather than duplicated at the console.</para>
/// </summary>
public sealed class GetSiteInstallationHandler(
    ISiteRepository sites,
    IPermissionChecker permissions,
    ISiteInstallationSignalRepository signals,
    IConversationReadStore conversations,
    IClock clock,
    SiteInstallationOptions options)
{
    public async Task<Result<SiteInstallationDto>> HandleAsync(GetSiteInstallation query, CancellationToken cancellationToken)
    {
        var allowed = await permissions.HasPermissionAsync(
            query.RequestedBy, query.SiteId, Permission.SiteConfigure, cancellationToken);
        if (!allowed)
        {
            return ConversationErrors.Forbidden("Operator does not have permission to view this site's installation details.");
        }

        var site = await sites.GetByIdAsync(query.SiteId, cancellationToken);
        if (site is null)
        {
            return ConversationErrors.SiteNotFound(query.SiteId.Value);
        }

        var signal = await signals.GetAsync(query.SiteId, cancellationToken);
        var mostRecentConversationAt = await conversations.GetMostRecentCreatedAtAsync(query.SiteId, cancellationToken);

        var now = clock.UtcNow;
        var threshold = TimeSpan.FromDays(options.RecentlyThresholdDays);
        var usedRecently = mostRecentConversationAt is { } createdAt && now - createdAt <= threshold;

        var state = SiteInstallationStateResolver.Resolve(
            signal.LastSeenAt, signal.LastRefusedOrigin, signal.LastRefusedOriginAt, usedRecently);

        return new SiteInstallationDto(
            site.PublicKey, site.AllowedOrigins,
            signal.FirstSeenAt, signal.LastSeenAt, signal.LastRefusedOrigin, signal.LastRefusedOriginAt,
            usedRecently, state);
    }
}
