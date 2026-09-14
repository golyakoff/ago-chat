using Ago.Chat.Api.Http;
using Ago.Chat.Application.UseCases.DisconnectNonEntitledChannelCredentialsAsOwner;
using Ago.Chat.Application.UseCases.ListNonEntitledChannelCredentialsAsOwner;
using Ago.Chat.Domain;

namespace Ago.Chat.Api.Owner;

/// <summary>
/// `23-85`/`adr/0151`: the platform owner's own two-step walkthrough for accounts connected without a
/// channel entitlement - `GET .../non-entitled-credentials` lists them, `POST .../disconnect` acts on
/// exactly the ids the owner names back. Both cross-tenant, the same "the resource is all sites, so it
/// is its own route rather than nested under one site's own group" shape
/// <see cref="OwnerSuspensionEndpoints"/>'s own <c>/api/v1/owner/suspensions</c> already uses.
///
/// <para><b>This is the mechanism the backlog item's own hard requirement describes - and deliberately
/// never anything that runs by itself.</b> "This is not authorised to switch on silently: the author
/// wants to walk through the whole flow end to end, personally, before it runs for real" (the item's
/// own "Answered, 2026-09-09" section). There is no startup hook, no recurring job and no migration
/// that calls <see cref="DisconnectNonEntitledChannelCredentialsAsOwnerHandler"/> - the only caller is
/// this route, and the only way to reach it is a platform owner's own authenticated `POST`, naming the
/// exact ids they just read back from the `GET` above. A worker background job (the shape
/// `SubscriptionRenewalJob` uses for the *billing* half of entitlement) was deliberately not built for
/// this - that shape runs unattended, on a timer, which is exactly what this item's own author refused
/// to authorise for the first pass at an existing population of already-connected accounts.</para>
///
/// <para><b>Gated exclusively by <c>RequirePlatformOwner</c></b> - the entire access-control story for
/// both routes, the same single-gate shape every other owner surface in this codebase already uses:
/// neither handler this file resolves calls <c>IPermissionChecker</c>, and neither could (a cross-
/// tenant sweep is not a tenant-scoped permission to hold).</para>
/// </summary>
public static class OwnerChannelEntitlementEndpoints
{
    public static void MapOwnerChannelEntitlementEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/v1/owner/channel-entitlements")
            .RequireAuthorization("RequirePlatformOwner");

        group.MapGet("/non-entitled-credentials", HandleListAsync);
        group.MapPost("/disconnect", HandleDisconnectAsync);
    }

    private static async Task<IResult> HandleListAsync(
        ListNonEntitledChannelCredentialsAsOwnerHandler handler, CancellationToken cancellationToken)
    {
        var rows = await handler.HandleAsync(new ListNonEntitledChannelCredentialsAsOwner(), cancellationToken);
        return Results.Ok(new NonEntitledChannelCredentialsResponse(rows
            .Select(r => new NonEntitledChannelCredentialDto(
                r.ChannelCredentialId.Value, r.SiteId.Value, r.Kind.ToString(), r.CreatedAt))
            .ToArray()));
    }

    private static async Task<IResult> HandleDisconnectAsync(
        DisconnectChannelCredentialsRequest request,
        DisconnectNonEntitledChannelCredentialsAsOwnerHandler handler,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        if (request.ChannelCredentialIds is not { Count: > 0 })
        {
            return Results.Problem(
                statusCode: StatusCodes.Status400BadRequest,
                title: "At least one channelCredentialId is required - this route never disconnects an unreviewed set.");
        }

        var ids = request.ChannelCredentialIds.Select(id => new ChannelCredentialId(id)).ToArray();
        var outcomes = await handler.HandleAsync(
            new DisconnectNonEntitledChannelCredentialsAsOwner(ids), cancellationToken);

        return Results.Ok(new DisconnectChannelCredentialsResponse(outcomes
            .Select(o => new ChannelCredentialDisconnectOutcomeDto(o.ChannelCredentialId.Value, o.Status.ToString()))
            .ToArray()));
    }

    public sealed record NonEntitledChannelCredentialDto(Guid ChannelCredentialId, Guid SiteId, string Kind, DateTimeOffset CreatedAt);

    public sealed record NonEntitledChannelCredentialsResponse(IReadOnlyList<NonEntitledChannelCredentialDto> Credentials);

    /// <summary>`ChannelCredentialIds` is the reviewed set, always required and never defaulted to
    /// "everything the list endpoint would return right now" - see this file's own class remarks on
    /// why an implicit "disconnect everything unentitled" body is exactly what this route must never
    /// accept.</summary>
    public sealed record DisconnectChannelCredentialsRequest(IReadOnlyList<Guid> ChannelCredentialIds);

    public sealed record ChannelCredentialDisconnectOutcomeDto(Guid ChannelCredentialId, string Status);

    public sealed record DisconnectChannelCredentialsResponse(IReadOnlyList<ChannelCredentialDisconnectOutcomeDto> Outcomes);
}
