using Ago.Chat.Application.Abstractions;
using Ago.Chat.Domain;
using Ago.Platform.Kernel;

namespace Ago.Chat.Application.UseCases.GetWidgetConfig;

/// <summary>
/// `11-01`: an operator-authenticated, low-frequency admin read - deliberately **not** wrapped in
/// `ICache.GetOrCreateAsync` the way `GetSiteConfigByPublicKeyHandler`'s widget-handshake read is
/// (caching.md's "the hot one"). This is the console's own widget-config screen (`11-02`) checking
/// what is currently configured, not something every visitor's page load hits - a plain repository
/// read is the right default here, and a later session should not assume every site-scoped read
/// needs the same caching treatment just because a sibling one does.
/// </summary>
public sealed class GetWidgetConfigHandler(ISiteRepository sites, IPermissionChecker permissions)
{
    public async Task<Result<WidgetConfigDto>> HandleAsync(GetWidgetConfig query, CancellationToken cancellationToken)
    {
        var allowed = await permissions.HasPermissionAsync(
            query.RequestedBy, query.SiteId, Permission.SiteConfigure, cancellationToken);
        if (!allowed)
        {
            return ConversationErrors.Forbidden("Operator does not have permission to view this site's widget configuration.");
        }

        var site = await sites.GetByIdAsync(query.SiteId, cancellationToken);
        if (site is null)
        {
            return ConversationErrors.SiteNotFound(query.SiteId.Value);
        }

        return new WidgetConfigDto(
            site.WidgetConfig.PrimaryColorHex,
            site.WidgetConfig.Position,
            site.Locale,
            site.WidgetConfig.NoticeText,
            site.WidgetConfig.NoticeUrl);
    }
}
