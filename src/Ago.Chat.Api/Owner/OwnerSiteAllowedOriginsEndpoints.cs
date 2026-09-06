using Ago.Chat.Api.Http;
using Ago.Chat.Application.UseCases.UpdateSiteAllowedOriginsAsOwner;
using Ago.Chat.Domain;

namespace Ago.Chat.Api.Owner;

/// <summary>
/// `23-48`: the platform owner's own write for a tenant's allowed origins - see
/// <see cref="UpdateSiteAllowedOriginsAsOwner"/>'s own remarks for the full argument that this is not
/// a second, owner-only copy of a self-service write: no such write has ever existed
/// (`docs/backlog/23-46-*.md`'s own finding, "there is no editor for a chat tenant's allowed origin
/// anywhere in this console").
///
/// <para><b>A deliberately separate file and route from <see cref="OwnerSitesEndpoints"/></b> - the
/// same "own Map call, own file" discipline that file's own class remarks state for itself and for
/// <see cref="OwnerModuleEndpoints"/>: several integration tests build a stripped-down
/// <c>WebApplication</c> that maps only the routes one test needs, and folding an unrelated write into
/// an existing read-only Map call would make every one of those tests an undeclared dependent of a
/// handler they never intended to resolve (`OwnerSitesEndpoints`'s own remarks explain the exact
/// failure mode this avoids).</para>
///
/// <para><b>Gated exclusively by <c>RequirePlatformOwner</c></b> - the entire access-control story,
/// the same single-gate shape every owner surface in this codebase already uses:
/// <see cref="UpdateSiteAllowedOriginsAsOwnerHandler"/> calls no
/// <see cref="Application.Abstractions.IPermissionChecker"/> and could not (that handler's own
/// remarks), which is precisely why this route must never be mapped with any weaker policy.</para>
///
/// <para><b><c>PUT</c>, not <c>PATCH</c></b> - the request body is the complete replacement list, the
/// same "whole value, not a delta" shape <see cref="OwnerModuleEndpoints"/>'s own grant body uses for
/// <c>TriggerWords</c>, and matching how <see cref="Domain.Site.UpdateAllowedOrigins"/> itself is
/// shaped (a full replace, never an add/remove pair).</para>
///
/// <para><b>Deliberately writes no <c>access_records</c> row, unlike every sibling owner write</b>
/// (<c>OwnerModuleEndpoints</c>'s <c>OwnerModuleGrant</c>/<c>OwnerModuleRevoke</c>,
/// <c>OwnerChannelIdentityEndpoints</c>'s <c>OwnerChannelIdentityUnlink</c>). Recording this write the
/// same way would need a new <c>AccessRecordKind</c> member, and that enum's values are enumerated
/// verbatim in <c>ck_access_records_access_kind</c>
/// (<c>AccessRecordEntityConfiguration</c>) - adding one is a real schema migration, which this
/// item's own brief says to stop and report rather than add. Reported as a gap in this item's own
/// hand-off rather than silently worked around (e.g. by misusing an existing, semantically wrong
/// <c>AccessRecordKind</c> value) - a follow-up item can add the member and its migration together
/// once the author has seen this.</para>
/// </summary>
public static class OwnerSiteAllowedOriginsEndpoints
{
    public static void MapOwnerSiteAllowedOriginsEndpoint(this WebApplication app)
    {
        app.MapPut("/api/v1/owner/sites/{siteId:guid}/allowed-origins", HandleUpdateAsync)
            .RequireAuthorization("RequirePlatformOwner");
    }

    private static async Task<IResult> HandleUpdateAsync(
        Guid siteId,
        UpdateAllowedOriginsRequest request,
        UpdateSiteAllowedOriginsAsOwnerHandler handler,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        var result = await handler.HandleAsync(
            new UpdateSiteAllowedOriginsAsOwner(new SiteId(siteId), request.AllowedOrigins), cancellationToken);

        if (result.IsFailure)
        {
            return result.Error!.Value.ToProblem(httpContext);
        }

        return Results.Ok(new UpdateAllowedOriginsResponse(result.Value));
    }

    public sealed record UpdateAllowedOriginsRequest(IReadOnlyList<string> AllowedOrigins);

    /// <summary>Echoes the deduplicated, saved list back - not merely 200 with an empty body - so the
    /// console can show the exact value now in force without a second `GET`
    /// (`UpdateSiteAllowedOriginsAsOwnerHandler`'s own dedup, applied before this response is
    /// built).</summary>
    public sealed record UpdateAllowedOriginsResponse(IReadOnlyList<string> AllowedOrigins);
}
