using Ago.Chat.Api.Http;
using Ago.Chat.Application.Abstractions;
using Ago.Chat.Application.UseCases.EnableModuleForSiteAsOwner;
using Ago.Chat.Application.UseCases.RevokeModuleForSiteAsOwner;
using Ago.Chat.Domain;
using Ago.Platform.Kernel;

namespace Ago.Chat.Api.Owner;

/// <summary>
/// `22-17`: the platform owner's own module grant/revoke - a deliberate cross-tenant write
/// (this item's own brief), for two ordinary commercial motions self-service cannot cover: a trial
/// given without payment, and restoring a registration a failed payment should have created
/// (`22-07`'s own "payment succeeded, provisioning did not"). A deliberately separate route and file
/// from <see cref="Modules.ModuleEndpoints"/>, the same "`/owner/` stays the platform owner's own
/// namespace, never blurred with a site-scoped operator route" discipline
/// <see cref="OwnerChannelIdentityEndpoints"/>'s own remarks state for itself - even though both
/// ultimately reach the identical <c>EnabledModule</c> aggregate and the identical `22-11` module-first
/// registration mechanism.
///
/// <para><b>Gated exclusively by <c>RequirePlatformOwner</c></b> - the entire access-control story for
/// both routes, the same single-gate shape every other owner surface in this codebase already uses:
/// neither handler this file resolves calls <see cref="Application.Abstractions.IPermissionChecker"/>,
/// and could not (see <see cref="EnableModuleForSiteAsOwnerHandler"/>'s own remarks for why), which is
/// precisely why this route must never be mapped with any weaker policy.</para>
///
/// <para><b>Generic across every module, not calendar-specific</b> - the identical
/// <c>ModuleKeyLiteralRule</c> discipline <see cref="Modules.ModuleEndpoints"/>'s own remarks describe:
/// the module key is a request body field here too, never a literal this file names.</para>
/// </summary>
public static class OwnerModuleEndpoints
{
    public static void MapOwnerModuleEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/v1/owner/sites/{siteId:guid}/modules")
            .RequireAuthorization("RequirePlatformOwner");

        group.MapPut("", HandleGrantAsync);
        group.MapDelete("/{moduleKey}", HandleRevokeAsync);
    }

    private static async Task<IResult> HandleGrantAsync(
        Guid siteId,
        GrantModuleRequest request,
        EnableModuleForSiteAsOwnerHandler handler,
        IAccessRecordRepository accessRecords,
        IClock clock,
        IIdGenerator idGenerator,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        var result = await handler.HandleAsync(
            new EnableModuleForSiteAsOwner(
                new SiteId(siteId), request.ModuleKey, request.TriggerWords, request.EntryPoint, request.Credential,
                request.ProvisioningSecret, request.ExpiresAt),
            cancellationToken);

        if (result.IsFailure)
        {
            return result.Error!.Value.ToProblem(httpContext);
        }

        // `24-12`: resourceId is the freshly minted EnabledModuleId - the row this write actually
        // brought into existence, the same id GrantModuleResponse never echoes back today.
        await OwnerAccessRecorder.RecordAsync(
            httpContext, accessRecords, clock, idGenerator, AccessRecordKind.OwnerModuleGrant,
            new SiteId(siteId), AccessRecordResourceKind.EnabledModule, result.Value.Value, cancellationToken);

        return Results.Ok(new GrantModuleResponse(request.ModuleKey, request.TriggerWords, request.EntryPoint, request.ExpiresAt));
    }

    private static async Task<IResult> HandleRevokeAsync(
        Guid siteId,
        string moduleKey,
        // `ModuleEndpoints.HandleRevokeAsync`'s own remarks: DELETE does not allow an inferred body
        // parameter, found running by that item's own integration test.
        [Microsoft.AspNetCore.Mvc.FromBody] RevokeModuleAsOwnerRequest request,
        RevokeModuleForSiteAsOwnerHandler handler,
        IAccessRecordRepository accessRecords,
        IClock clock,
        IIdGenerator idGenerator,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        var result = await handler.HandleAsync(
            new RevokeModuleForSiteAsOwner(new SiteId(siteId), moduleKey, request.ProvisioningSecret), cancellationToken);

        if (result.IsFailure)
        {
            return result.Error!.Value.ToProblem(httpContext);
        }

        // `24-12`: no resourceId here, deliberately - RevokeModuleForSiteAsOwner names the row it acts
        // on by (SiteId, moduleKey), not by EnabledModuleId, and resolving the id first would be a
        // second lookup this item does not otherwise need. ResourceKind alone (plus SiteId, already on
        // the row) still says which kind of thing was revoked, even without the specific row's own id.
        await OwnerAccessRecorder.RecordAsync(
            httpContext, accessRecords, clock, idGenerator, AccessRecordKind.OwnerModuleRevoke,
            new SiteId(siteId), AccessRecordResourceKind.EnabledModule, resourceId: null, cancellationToken);

        return Results.Ok();
    }

    /// <summary>
    /// A property-declared record rather than this file's usual positional shape (contrast
    /// <see cref="Modules.ModuleEndpoints.EnableModuleRequest"/>) for exactly one reason:
    /// <see cref="ExpiresAt"/> needs the <see langword="required"/> modifier, which C#'s positional
    /// record syntax has no way to express on an individual parameter. <see langword="required"/> on a
    /// nullable member is what makes this item's own "decide, don't default" rule mechanical rather
    /// than a convention an implementer could forget: System.Text.Json refuses to deserialize a body
    /// that omits the <c>expiresAt</c> key at all (a 400, before this handler ever runs), while a body
    /// that includes it as JSON <c>null</c> - the deliberate "no end date" choice - deserializes
    /// normally. Omitting the key and choosing <c>null</c> are different acts under this shape; they
    /// are indistinguishable under a plain optional property.
    /// </summary>
    /// <param name="Credential">Never echoed back - the same hygiene
    /// <see cref="Modules.ModuleEndpoints.EnableModuleRequest"/>'s own remarks describe.</param>
    /// <param name="ProvisioningSecret">`22-11`: proves this call may provision on the module
    /// deployment's own behalf.</param>
    /// <param name="ExpiresAt">See <see cref="EnableModuleForSiteAsOwner"/>'s own remarks for the full
    /// argument for why this is required rather than optional.</param>
    public sealed class GrantModuleRequest
    {
        public required string ModuleKey { get; init; }

        public required IReadOnlyList<string> TriggerWords { get; init; }

        public required string EntryPoint { get; init; }

        public required string Credential { get; init; }

        public required string ProvisioningSecret { get; init; }

        public required DateTimeOffset? ExpiresAt { get; init; }
    }

    public sealed record GrantModuleResponse(
        string ModuleKey, IReadOnlyList<string> TriggerWords, string EntryPoint, DateTimeOffset? ExpiresAt);

    public sealed record RevokeModuleAsOwnerRequest(string ProvisioningSecret);
}
