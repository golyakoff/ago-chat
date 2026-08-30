using Ago.Chat.Api.Http;
using Ago.Chat.Application.UseCases.UnlinkChannelIdentityAsOwner;
using Ago.Chat.Domain;

namespace Ago.Chat.Api.Owner;

/// <summary>
/// `14-12`/`adr/0079`: the platform owner's own unconditional unlink - the first write/action surface
/// this actor has ever had (`authorization.md`'s own actor table, as of this item, named exactly one:
/// `GET /api/v1/owner/sites`, read-only). A deliberately separate file and route from
/// <c>ChannelIdentityEndpoints</c>, the same "no path segment shared between the site Admin's surface and
/// the owner's" discipline <see cref="OwnerSitesEndpoints"/>'s own remarks state for itself - `/owner/`
/// stays the platform owner's own namespace, never blurred with a site-scoped operator route even though
/// both ultimately call the identical domain mutation.
///
/// <para><b>Gated exclusively by <c>RequirePlatformOwner</c></b> - the entire access-control story for
/// this route, the same single-gate shape <see cref="OwnerSitesEndpoints"/> already uses:
/// <c>UnlinkChannelIdentityAsOwnerHandler</c> makes no second check and could not (that handler's own
/// remarks say why), which is precisely why this route must never be mapped with any weaker policy.</para>
/// </summary>
public static class OwnerChannelIdentityEndpoints
{
    public static void MapOwnerChannelIdentityEndpoints(this WebApplication app)
    {
        app.MapPost(
                "/api/v1/owner/sites/{siteId:guid}/channel-identities/{channelIdentityId:guid}/unlink",
                HandleUnlinkAsync)
            .RequireAuthorization("RequirePlatformOwner");
    }

    private static async Task<IResult> HandleUnlinkAsync(
        Guid siteId,
        Guid channelIdentityId,
        UnlinkChannelIdentityAsOwnerHandler handler,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        var result = await handler.HandleAsync(
            new UnlinkChannelIdentityAsOwner(new SiteId(siteId), new ChannelIdentityId(channelIdentityId)),
            cancellationToken);

        return result.IsFailure ? result.Error!.Value.ToProblem(httpContext) : Results.NoContent();
    }
}
