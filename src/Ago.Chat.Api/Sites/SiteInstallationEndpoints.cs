using Ago.Chat.Api.Auth;
using Ago.Chat.Api.Http;
using Ago.Chat.Application.UseCases.GetSiteInstallation;
using Ago.Chat.Domain;

namespace Ago.Chat.Api.Sites;

/// <summary>
/// `10-06`: `GET /api/v1/sites/{siteId}/installation` - the read a tenant needs to put the widget on
/// their own shop, which nothing in this codebase returned before this item (`GetSiteInstallationHandler`'s
/// own doc comment has the gap). `RequireOperatorIdentity` plus a route-level `SiteId`, the identical
/// convention `WidgetConfigEndpoints`/`OfflineAutoReplyEndpoints` already use for a site-scoped,
/// operator-only admin read: an operator's own site claim is not necessarily the site being installed.
///
/// <para><b>Its own file, not folded into <see cref="SitesEndpoints"/>.</b> <c>SitesEndpoints.cs</c>
/// already bundles registration, erasure, export and message-archive routes behind one file, several of
/// which pull in dependencies (blob storage, the archive read store) this read has nothing to do with.
/// The alternative - adding this route to that file since it is also "about the Site resource itself" -
/// was rejected specifically so a test exercising only this route does not have to stand up handlers for
/// every other one; <c>WidgetConfigEndpoints</c>/<c>OfflineAutoReplyEndpoints</c>/<c>WebhookEndpoints</c>
/// already established "one composable <c>Map...Endpoints</c> extension per concern" for exactly this
/// reason, and this follows that precedent rather than <c>SitesEndpoints</c>' own broader one.</para>
/// </summary>
public static class SiteInstallationEndpoints
{
    public static void MapSiteInstallationEndpoints(this WebApplication app)
    {
        app.MapGet("/api/v1/sites/{siteId:guid}/installation", HandleGetAsync)
            .RequireAuthorization("RequireOperatorIdentity");
    }

    private static async Task<IResult> HandleGetAsync(
        Guid siteId, GetSiteInstallationHandler handler, HttpContext httpContext, CancellationToken cancellationToken)
    {
        var user = httpContext.User;
        var result = await handler.HandleAsync(
            new GetSiteInstallation(new SiteId(siteId), user.GetOperatorId()), cancellationToken);

        return result.IsFailure ? result.Error!.Value.ToProblem(httpContext) : Results.Ok(ToResponse(result.Value));
    }

    private static SiteInstallationResponse ToResponse(SiteInstallationDto dto) =>
        new(dto.PublicKey, dto.AllowedOrigins);

    public sealed record SiteInstallationResponse(string PublicKey, IReadOnlyList<string> AllowedOrigins);
}
