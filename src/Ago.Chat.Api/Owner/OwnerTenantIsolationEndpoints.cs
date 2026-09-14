using System.Reflection;
using System.Text.RegularExpressions;
using Ago.Chat.Api.Hubs;
using Ago.Chat.Application.Abstractions;
using Ago.Chat.Contracts;
using Ago.Platform.Kernel;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.SignalR;

namespace Ago.Chat.Api.Owner;

/// <summary>
/// `24-17`: `GET /api/v1/owner/tenant-isolation` - `docs/architecture/tenant-isolation.md`'s five
/// headline counts, computed from the assembly and the route table this process is actually running,
/// rather than read from a document somebody has to remember to re-run a scan and re-type.
///
/// <para><b>Gated exclusively by `RequirePlatformOwner`</b>, the identical single-gate shape every
/// other file in this folder already uses - and the reason matters more here than anywhere else in
/// it: the backlog item's own words are "47 entry points that do not check permissions is a hint for
/// somebody looking for a way in", so this is shown to the platform owner and to nobody else, not to
/// tenants and not in any anonymous response.</para>
///
/// <para><b>Own file, own Map call</b> - the same "own file, own registration" discipline
/// `OwnerPricingEndpoints`/`OwnerSiteAllowedOriginsEndpoints` above it already follow, for the
/// identical reason `OwnerSitesEndpoints`' own remarks give: a stripped-down test host that maps only
/// one owner surface must never be made to resolve a handler or a port it never asked for.</para>
///
/// <para><b>No `OwnerAccessRecorder` write here</b> - unlike every other file in this folder. `24-12`'s
/// own Scope is deliberately narrow: "recording everything is a second copy of the traffic and a
/// personal-data store in its own right", and its <c>AccessRecordKind</c> members are each one
/// specific tenant's own business data read across a boundary. This endpoint returns none of that -
/// no site name, no tenant's row, nothing `personal-data.md` classifies - only a structural fact about
/// this deployment's own code, so recording an access to it would be widening `24-12`'s own considered
/// restraint rather than following it.</para>
/// </summary>
public static class OwnerTenantIsolationEndpoints
{
    /// <summary>`24-17`. One HTTP route excluded from <see cref="RoutesAndHubMethodsAsync"/> by
    /// inspection (no handler, no site) - the identical exclusion `scan_routes.py` already documents
    /// for the same reason.</summary>
    private static readonly string[] ExcludedPaths = ["/healthz/version"];

    /// <summary>A resolved path containing a literal `{siteId}` route parameter, with or without a
    /// route constraint (`{siteId:guid}`) - the same pattern `scan_routes.py` matches, applied to this
    /// process's own live `RoutePattern.RawText` instead of source text.</summary>
    private static readonly Regex ClientSuppliedSiteIdSegment =
        new(@"\{siteId(:[a-z]+)?\}", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static void MapOwnerTenantIsolationEndpoint(this WebApplication app)
    {
        app.MapGet("/api/v1/owner/tenant-isolation", HandleGetSummaryAsync)
            .RequireAuthorization("RequirePlatformOwner");
    }

    private static async Task<IResult> HandleGetSummaryAsync(
        ITenantScopeInspector inspector,
        EndpointDataSource endpoints,
        IClock clock,
        CancellationToken cancellationToken)
    {
        var snapshot = await inspector.GetSnapshotAsync(cancellationToken);
        var (routesAndHubMethods, clientSuppliedSiteIdRoutes) = RoutesAndHubMethods(endpoints);

        var response = new TenantIsolationSummaryResponse(
            snapshot.EntryPoints,
            snapshot.HandlerClasses,
            snapshot.RbacGated,
            snapshot.ExemptListed,
            snapshot.UnaccountedKeys,
            snapshot.ExemptButAlsoLooksGated,
            routesAndHubMethods,
            clientSuppliedSiteIdRoutes,
            clock.UtcNow);

        return Results.Ok(response);
    }

    /// <summary>Row 4 and row 5 of the headline table, read from this process's own live route table
    /// (`EndpointDataSource`, populated by every `Map*` call `Program.cs` makes - not re-derived from
    /// `Ago.Chat.Api`'s source text the way `scan_routes.py` has to) plus reflection over the two hub
    /// classes, mirroring `scan_routes.py`'s own hub-method rule exactly (every public method except
    /// the two SignalR lifecycle callbacks).
    ///
    /// <para>Kept in this file rather than behind a port: unlike the entry-point figures above, "what
    /// routes does this specific ASP.NET Core application have" is a fact about being hosted over
    /// HTTP - `Ago.Chat.Application` must never know that, so there is no port to declare for it in
    /// `Application/Abstractions` without leaking a hosting concern into a layer the dependency rule
    /// keeps free of one. This is squarely `Ago.Chat.Api`'s own business, the same way
    /// `HubOriginValidator`/`ConsoleOriginValidator` already are.</para>
    /// </summary>
    private static (int RoutesAndHubMethods, int ClientSuppliedSiteIdRoutes) RoutesAndHubMethods(
        EndpointDataSource endpoints)
    {
        var routes = new HashSet<(string Method, string Path)>();
        foreach (var endpoint in endpoints.Endpoints.OfType<RouteEndpoint>())
        {
            var rawPath = endpoint.RoutePattern.RawText;
            if (string.IsNullOrEmpty(rawPath))
            {
                continue;
            }

            var path = rawPath.StartsWith('/') ? rawPath : "/" + rawPath;
            if (ExcludedPaths.Contains(path))
            {
                continue;
            }

            var methods = endpoint.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods;
            if (methods is null)
            {
                continue;
            }

            foreach (var method in methods)
            {
                routes.Add((method, path));
            }
        }

        var clientSupplied = routes.Count(r => ClientSuppliedSiteIdSegment.IsMatch(r.Path));

        var hubMethods = CountHubMethods(typeof(OperatorHub)) + CountHubMethods(typeof(VisitorHub));

        return (routes.Count + hubMethods, clientSupplied);
    }

    /// <summary>Every public instance method a hub declares directly, other than the two SignalR
    /// lifecycle callbacks (`OnConnectedAsync`/`OnDisconnectedAsync`) - the identical exclusion
    /// `scan_routes.py`'s own remarks state, read here via <see cref="BindingFlags.DeclaredOnly"/>
    /// reflection over the real type rather than a source-text regex.</summary>
    private static int CountHubMethods(Type hubType) =>
        hubType
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Count(method => !method.IsSpecialName
                && method.Name is not (nameof(Hub.OnConnectedAsync) or nameof(Hub.OnDisconnectedAsync)));
}
