using Ago.Chat.Application.UseCases.GetSiteConfigById;
using Ago.Chat.Domain;
using Microsoft.AspNetCore.SignalR;

namespace Ago.Chat.Api.Cors;

/// <summary>
/// `5-01`, layer 2 for hub connections specifically. `SiteOriginCorsPolicyProvider` (layer 1) only
/// covers plain HTTP - a WebSocket upgrade's `Origin` header is not subject to the same browser
/// preflight/policy mechanism at all, so for `VisitorHub`/`OperatorHub` this check is not
/// defense-in-depth, it is the *only* enforcement point. Shared between both hubs rather than
/// duplicated: the logic (read `Origin` off the underlying `HttpContext`, resolve the site the
/// connection's own JWT claims, compare) is identical for both, only which claim supplies the
/// `SiteId` differs, and that is already the caller's job to pass in.
/// </summary>
public sealed class HubOriginValidator(GetSiteConfigByIdHandler getSiteConfig)
{
    public async Task<bool> IsAllowedAsync(HubCallerContext context, SiteId siteId)
    {
        var origin = context.GetHttpContext()?.Request.Headers.Origin.ToString();
        if (string.IsNullOrEmpty(origin))
        {
            // No cross-origin claim to verify - a same-origin caller (the dev harness, local-dev.md)
            // or a non-browser client (nothing stops one from opening a WebSocket without sending
            // Origin at all).
            return true;
        }

        var site = await getSiteConfig.HandleAsync(new GetSiteConfigById(siteId), context.ConnectionAborted);
        return site is not null && site.AllowedOrigins.Contains(origin);
    }
}
