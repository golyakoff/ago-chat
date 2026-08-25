using Ago.Chat.Application.UseCases.ListSitesForOwner;

namespace Ago.Chat.Api.Owner;

/// <summary>
/// `12-02`: the platform owner's cross-tenant operations read API - the one HTTP surface in this
/// codebase that is not scoped to a single site.
///
/// <para><b>`/owner/`, deliberately not `/admin/`.</b> `authorization.md` distinguishes two actors
/// that the word "admin" would collapse back together: `5-08`'s site-scoped `"Admin"` role (a
/// tenant's own supervisor, holding `site:configure` *for their site*) and the platform owner (the
/// operator of the service itself, `adr/0032`). A URL is documentation that ends up in logs, bug
/// reports and screenshots, so it should not blur a boundary the authorization model draws sharply;
/// `GET /api/v1/conversations/all` is the site Admin's surface, this is the owner's, and no path
/// segment is shared between them.</para>
///
/// <para><b>Gated exclusively by `RequirePlatformOwner`</b> (`12-01`, `Program.cs`). That policy is
/// the entire access-control story for the cross-tenant read behind it - the handler makes no second
/// check and could not (<see cref="ListSitesForOwnerHandler"/> says why), which is precisely why this
/// route must never be mapped with any weaker policy, and why it is the only route in the
/// application that resolves that handler.</para>
/// </summary>
public static class OwnerSitesEndpoints
{
    public static void MapOwnerEndpoints(this WebApplication app)
    {
        // `?before=&limit=` verbatim from api-design.md's pagination rule, rather than the
        // `?beforeId=&pageSize=` spelling `5-08`'s site-scoped list happens to use - this is a new
        // surface with no client to keep compatible, so it follows the convention as written. No
        // `OFFSET` and no page numbers exist anywhere behind it (`data-model.md`).
        app.MapGet("/api/v1/owner/sites", HandleListSitesAsync)
            .RequireAuthorization("RequirePlatformOwner");
    }

    private static async Task<IResult> HandleListSitesAsync(
        Guid? before,
        int? limit,
        ListSitesForOwnerHandler handler,
        CancellationToken cancellationToken)
    {
        // No Result<T>/ToProblem branch here, unlike the endpoints around it: this query has no
        // failure mode of its own to translate. There is no id to be not-found (the resource is "all
        // sites"), no permission for the handler to refuse (the policy above already decided), and no
        // caller input that can be invalid - an out-of-range `limit` is clamped rather than rejected,
        // since a 400 for asking for too many rows would tell an operator nothing a page of results
        // does not. A failing query surfaces as the host's own problem-details 500, which is the
        // truthful answer for "the database did not respond".
        var response = await handler.HandleAsync(new ListSitesForOwner(before, limit), cancellationToken);

        return Results.Ok(response);
    }
}
