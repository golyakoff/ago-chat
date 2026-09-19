using Ago.Chat.Application.Abstractions;
using Ago.Chat.Domain;
using Ago.Platform.Kernel;

namespace Ago.Chat.Application.UseCases.GetSiteBranding;

public sealed class GetSiteBrandingHandler(
    ISiteRepository sites, IPermissionChecker permissions, ISiteLogoPublicUrlBuilder logoUrls)
{
    public async Task<Result<SiteBrandingDto>> HandleAsync(GetSiteBranding query, CancellationToken cancellationToken)
    {
        var allowed = await permissions.HasPermissionAsync(
            query.RequestedBy, query.SiteId, Permission.SiteConfigure, cancellationToken);
        if (!allowed)
        {
            return ConversationErrors.Forbidden("Operator does not have permission to view this site's branding.");
        }

        var site = await sites.GetByIdAsync(query.SiteId, cancellationToken);
        if (site is null)
        {
            return ConversationErrors.SiteNotFound(query.SiteId.Value);
        }

        var logoUrl = site.HasLogo ? logoUrls.Build(site.LogoObjectKey!) : null;
        return new SiteBrandingDto(site.BrandCompanyName, logoUrl, site.LogoStatus, site.LogoRejectionReason);
    }
}
