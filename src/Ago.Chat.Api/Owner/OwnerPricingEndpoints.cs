using Ago.Chat.Api.Http;
using Ago.Chat.Application.UseCases.GetPricingForOwner;
using Ago.Chat.Application.UseCases.PublishPriceVersion;

namespace Ago.Chat.Api.Owner;

/// <summary>
/// `25-20`: the platform owner's own price-list read - `GET /api/v1/owner/pricing`. Every currently-
/// paid capability's price, sourced from the identical configuration the billing code itself charges
/// from (see <see cref="GetPricingForOwnerHandler"/>'s own remarks), so the owner can check the
/// product's own numbers against `ago-business`'s decision documents without opening that private
/// repository.
///
/// <para><b>Gated exclusively by `RequirePlatformOwner`</b> - the identical single-gate shape every
/// other owner surface in this codebase already uses (<c>OwnerSitesEndpoints</c>'s own remarks): the
/// handler this route resolves makes no second permission check and could not, since the fact that
/// authorizes this call is a Keycloak realm role, never a row in a table
/// <c>IPermissionChecker</c> could query.</para>
///
/// <para><b>Its own file, its own `Map` call</b> - the same "own file, own registration" discipline
/// every owner surface in this file's sibling list already follows
/// (<c>OwnerSiteAllowedOriginsEndpoints</c>'s own remarks give the full reasoning: a test host that
/// maps only one owner surface must never be made to resolve a handler it never intended to register
/// just because two routes happened to share a file).</para>
///
/// <para><b>No <c>OwnerAccessRecorder.RecordAsync</c> call, unlike every other read in this
/// namespace.</b> `24-12`'s own Scope draws this deliberately narrow: an access record exists for a
/// boundary-crossing read of *tenant* data reached by nobody who is a party to that tenant
/// (<see cref="Domain.AccessRecordKind"/>'s own remarks, `OwnerSiteList`/`OwnerSiteDetail`). This
/// route reads none - the response carries the platform's own configured pricing, identical for every
/// tenant and every caller who could ever reach this policy, never a fact about a named site or a
/// named person. `24-12`'s own "recording everything is a second copy of the traffic and a personal-
/// data store in its own right" is the reason not to add a member here that would fire on every page
/// load of a screen with nothing tenant-scoped to attest to.</para>
/// </summary>
public static class OwnerPricingEndpoints
{
    public static void MapOwnerPricingEndpoint(this WebApplication app)
    {
        app.MapGet("/api/v1/owner/pricing", HandleGetPricingAsync)
            .RequireAuthorization("RequirePlatformOwner");

        // `25-43`: the write side this screen has never had - a new version for an already-registered
        // key, never a new key (PublishPriceVersionHandler's own PricedResourceKeys.IsKnown guard is
        // what actually enforces that; this route adds no second check of its own, the identical
        // "the handler is the one place that decides, the route only gates who may call it" shape
        // OwnerDocumentEndpoints' own remarks state for the equivalent document-publish write).
        app.MapPost("/api/v1/owner/prices/{key}/versions", HandlePublishPriceVersionAsync)
            .RequireAuthorization("RequirePlatformOwner");
    }

    private static async Task<IResult> HandleGetPricingAsync(
        GetPricingForOwnerHandler handler, CancellationToken cancellationToken)
    {
        // No Result<T>/ToProblem branch, matching HandleListSitesAsync's own reasoning: this read has
        // no failure mode of its own to translate - no id to be not-found, no caller input to be
        // invalid, no permission left for a handler to refuse once the policy above already decided.
        var response = await handler.HandleAsync(cancellationToken);

        return Results.Ok(response);
    }

    private static async Task<IResult> HandlePublishPriceVersionAsync(
        string key, PublishPriceVersionRequest request, PublishPriceVersionHandler handler, HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        var result = await handler.HandleAsync(new PublishPriceVersion(key, request.AmountRub), cancellationToken);

        if (result.IsFailure)
        {
            return result.Error!.Value.ToProblem(httpContext);
        }

        var dto = result.Value;
        return Results.Ok(new PublishedPriceVersionResponse(dto.Key, dto.Version, dto.Sequence, dto.AmountRub, dto.PublishedAt));
    }

    public sealed record PublishPriceVersionRequest(decimal AmountRub);

    public sealed record PublishedPriceVersionResponse(string Key, string Version, int Sequence, decimal AmountRub, DateTimeOffset PublishedAt);
}
