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
/// </summary>
public sealed class GetSiteInstallationHandler(ISiteRepository sites, IPermissionChecker permissions)
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

        return new SiteInstallationDto(site.PublicKey, site.AllowedOrigins);
    }
}
