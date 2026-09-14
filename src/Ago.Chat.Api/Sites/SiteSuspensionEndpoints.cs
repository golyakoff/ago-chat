using Ago.Chat.Api.Auth;
using Ago.Chat.Api.Http;
using Ago.Chat.Application.UseCases.GetSuspensionStatusForSite;
using Ago.Chat.Domain;

namespace Ago.Chat.Api.Sites;

/// <summary>
/// `25-70`: `GET /api/v1/sites/{siteId}/suspension` - the tenant's own read of its own account's
/// suspension state, the missing half `docs/backlog/22-08-*.md`'s own Scope named
/// ("the console says the account is suspended, since when, until when, and what to do about it") but
/// whose Done-when never held anyone to. `RequireOperatorIdentity` plus a route-level <c>SiteId</c>, the
/// identical convention <see cref="SiteInstallationEndpoints"/>/<see cref="Modules.ModuleEndpoints"/>
/// already use for a site-scoped operator read - the handler, not this endpoint, is what actually scopes
/// the read to a site the caller may see (<see cref="GetSuspensionStatusForSiteHandler"/>'s own remarks).
///
/// <para><b>Its own file, not folded into <see cref="SitesEndpoints"/> or
/// <see cref="Modules.ModuleEndpoints"/>.</b> The identical "one composable <c>Map...Endpoints</c>
/// extension per concern" precedent <see cref="SiteInstallationEndpoints"/>'s own remarks state for
/// itself: a test exercising this one route has no reason to stand up every handler
/// <c>SitesEndpoints</c> bundles (blob storage, the archive read store, export), and a suspension read
/// has nothing to do with a module's own credential or entry point either.</para>
/// </summary>
public static class SiteSuspensionEndpoints
{
    public static void MapSiteSuspensionEndpoints(this WebApplication app)
    {
        app.MapGet("/api/v1/sites/{siteId:guid}/suspension", HandleGetAsync)
            .RequireAuthorization("RequireOperatorIdentity");
    }

    private static async Task<IResult> HandleGetAsync(
        Guid siteId, GetSuspensionStatusForSiteHandler handler, HttpContext httpContext, CancellationToken cancellationToken)
    {
        var result = await handler.HandleAsync(
            new GetSuspensionStatusForSite(httpContext.User.GetOperatorId(), new SiteId(siteId)), cancellationToken);

        return result.IsFailure ? result.Error!.Value.ToProblem(httpContext) : Results.Ok(
            new SiteSuspensionStatusResponse(result.Value.IsSuspended, result.Value.Since, result.Value.Until));
    }

    /// <param name="IsSuspended">Whether the caller's own site is suspended right now.</param>
    /// <param name="Since">When the suspension currently in effect began. <see langword="null"/> exactly
    /// when <paramref name="IsSuspended"/> is <see langword="false"/>.</param>
    /// <param name="Until">When the suspension currently in effect is due to lift on its own, absent an
    /// extension. <see langword="null"/> exactly when <paramref name="IsSuspended"/> is
    /// <see langword="false"/>.</param>
    public sealed record SiteSuspensionStatusResponse(bool IsSuspended, DateTimeOffset? Since, DateTimeOffset? Until);
}
