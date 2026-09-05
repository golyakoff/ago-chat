using Ago.Chat.Application.Abstractions;
using Ago.Chat.Application.UseCases.GetSiteForOwner;
using Ago.Chat.Application.UseCases.ListSitesForOwner;
using Ago.Chat.Api.Http;
using Ago.Chat.Domain;
using Ago.Platform.Kernel;

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
///
/// <para><b>`23-14`</b> gave the owner a per-tenant detail route, <c>GET /api/v1/owner/sites/{siteId:guid}</c>
/// - the companion to the list above, same gate, same "no second check" shape
/// (<see cref="GetSiteForOwnerHandler"/> says why). It is mapped by a **separate** method,
/// <see cref="MapOwnerSiteDetailEndpoint"/>, rather than folded into <see cref="MapOwnerEndpoints"/> -
/// the same "own file, own Map call" discipline `Program.cs` already applies to
/// `SiteInstallationEndpoints` beside `SitesEndpoints`. Found live, building this item: several
/// integration tests build a stripped-down `WebApplication` that calls `MapOwnerEndpoints()` alone to
/// exercise the list endpoint without registering every handler the full host does
/// (`PlatformOwnerAsTenantTests`, `OwnerSitesEndpointTests`) - folding the detail route into that same
/// method made `GetSiteForOwnerHandler` an undeclared dependency of hosts that never intended to
/// resolve it, and ASP.NET Core's Minimal API refuses to build *any* endpoint's metadata (a `GET`
/// cannot infer a body parameter) once one endpoint's service parameter cannot be recognised - so the
/// unrelated list endpoint failed too, in every test that never touches the detail route at all. Two
/// map calls is what keeps a test host's registrations matching exactly the routes it maps.</para>
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

    /// <summary>`23-14`: the per-tenant detail read, deliberately its own Map call - see this file's
    /// own class remarks for why it is not folded into <see cref="MapOwnerEndpoints"/> above.
    /// `{siteId:guid}` is what tells Minimal API's routing this segment is not another literal path
    /// (there is no site named "sites"), the same constraint every other `{siteId:guid}` route in this
    /// codebase already applies.</summary>
    public static void MapOwnerSiteDetailEndpoint(this WebApplication app)
    {
        app.MapGet("/api/v1/owner/sites/{siteId:guid}", HandleGetSiteAsync)
            .RequireAuthorization("RequirePlatformOwner");
    }

    private static async Task<IResult> HandleListSitesAsync(
        string? query,
        Guid? before,
        int? limit,
        ListSitesForOwnerHandler handler,
        IAccessRecordRepository accessRecords,
        IClock clock,
        IIdGenerator idGenerator,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        // No Result<T>/ToProblem branch here, unlike the endpoints around it: this query has no
        // failure mode of its own to translate. There is no id to be not-found (the resource is "all
        // sites"), no permission for the handler to refuse (the policy above already decided), and no
        // caller input that can be invalid - an out-of-range `limit` is clamped rather than rejected,
        // since a 400 for asking for too many rows would tell an operator nothing a page of results
        // does not. A failing query surfaces as the host's own problem-details 500, which is the
        // truthful answer for "the database did not respond". `23-14`'s `query` fits the identical
        // shape: any text at all is a legal (if perhaps zero-match) search, so there is nothing here to
        // reject either.
        var response = await handler.HandleAsync(new ListSitesForOwner(query, before, limit), cancellationToken);

        // `24-12`: recorded unconditionally reaching here - this call has no failure branch of its own
        // (see this method's own remarks above), so "reached this line" already means "succeeded".
        // SiteId is null: this read spans every tenant, not one - AccessRecordKind.OwnerSiteList's own
        // remarks.
        await OwnerAccessRecorder.RecordAsync(
            httpContext, accessRecords, clock, idGenerator, AccessRecordKind.OwnerSiteList,
            siteId: null, resourceKind: null, resourceId: null, cancellationToken);

        return Results.Ok(response);
    }

    /// <summary>`23-14`: unlike <see cref="HandleListSitesAsync"/>, this endpoint does have a real
    /// failure mode - the named site may not exist - so it is the first handler on this file to go
    /// through the ordinary <c>Result&lt;T&gt;</c>/<c>ToProblem</c> translation every other endpoint
    /// file in this codebase already uses.</summary>
    private static async Task<IResult> HandleGetSiteAsync(
        Guid siteId,
        GetSiteForOwnerHandler handler,
        IAccessRecordRepository accessRecords,
        IClock clock,
        IIdGenerator idGenerator,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        var result = await handler.HandleAsync(new GetSiteForOwner(new SiteId(siteId)), cancellationToken);

        if (result.IsFailure)
        {
            return result.Error!.Value.ToProblem(httpContext);
        }

        // `24-12`: only on the real Site.NotFound-free path - a caller who named a site that does not
        // exist read nothing, so nothing is recorded (this item's own "a read that fails authorisation
        // is not an access", extended to the identical "failed for any reason" case).
        await OwnerAccessRecorder.RecordAsync(
            httpContext, accessRecords, clock, idGenerator, AccessRecordKind.OwnerSiteDetail,
            new SiteId(siteId), resourceKind: null, resourceId: null, cancellationToken);

        return Results.Ok(result.Value);
    }
}
