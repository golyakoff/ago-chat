using Ago.Chat.Api.Auth;
using Ago.Chat.Api.Http;
using Ago.Chat.Application.UseCases.DisableModuleForSite;
using Ago.Chat.Application.UseCases.EnableModuleForSite;
using Ago.Chat.Application.UseCases.ListEnabledModulesForSite;
using Ago.Chat.Application.UseCases.SetModuleTriggerWordsForSite;
using Ago.Chat.Domain;

namespace Ago.Chat.Api.Modules;

/// <summary>
/// `19-03`/`22-11` built a tenant's own self-service write surface here: register a module, rotate
/// its credential, revoke it, verify its registration. `23-83`/`adr/0151` removed all four; `26-316`
/// brings back enable and disable (the on/off toggle a tenant admin actually needs), and rotate/verify
/// stay on the owner surface.
///
/// <para><b>Why enable/disable were gone, and why they are safe again.</b> `19-03`'s own comment welded
/// two ideas together - a tenant turning a product on for themselves, and proving to the module that the
/// <em>platform</em> authorised it (`adr/0095`'s deployment-wide
/// <see cref="Domain.ModuleProvisioningSecret"/>). Together they required a tenant operator, the least
/// trusted caller here, to hold a cross-tenant secret in a browser request body, so `adr/0151` removed
/// the route rather than repair it. What has changed since: `23-65`/`adr/0150` (the provisioning secret)
/// and `23-92`/`adr/0154` (the entry point) moved <em>both</em> of those inputs out of the request body
/// and into <c>Ago.Chat.Api</c>'s own configuration. With nothing secret left for a tenant to send,
/// `26-316`'s author decision (self-serve, option в) reinstates enable and disable as a one-click toggle:
/// <see cref="EnableModuleForSiteHandler"/> mints the per-site credential itself and reads the secret and
/// entry point from configuration, and <see cref="Application.Abstractions.IPermissionChecker"/> on
/// `site:configure` - not <c>RequirePlatformOwner</c> - is the gate, so the call only ever affects the
/// caller's own site.</para>
///
/// <para><b>Rotate and verify stay on the owner surface</b> (`23-83`,
/// <see cref="Api.Owner.OwnerModuleEndpoints"/>) - they still need the provisioning secret for an
/// operation a tenant has no reason to perform, so nothing this item adds re-opens the hole `adr/0151`
/// closed. The platform-owner grant path also stays as an override: a module a platform owner granted
/// (<see cref="Domain.EnabledModule.GrantedByOwner"/>) refuses the tenant's own `DELETE` here
/// (<see cref="DisableModuleForSiteHandler"/>).</para>
///
/// <para><b>Generic across every module, never calendar-specific.</b> The module key is a route
/// parameter and the trigger words are a request-body field; nothing in this file names "calendar" or
/// "faq" - the same `adr/0065` decision-2 boundary the read side already keeps.</para>
///
/// <para><b>The read stays</b> - a tenant seeing which products are on their account is ordinary and
/// carries no secret. <see cref="ListEnabledModulesForSiteHandler"/>, `Permission.SiteConfigure` via
/// `RequireOperatorIdentity`, unchanged from `23-01`.</para>
/// </summary>
public static class ModuleEndpoints
{
    public static void MapModuleEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/v1/sites/{siteId:guid}/modules")
            .RequireAuthorization("RequireOperatorIdentity");

        group.MapGet("", HandleGetAsync);

        // `26-316`: the tenant admin's own enable/disable, keyed by module in the path exactly like the
        // owner surface's own routes (`OwnerModuleEndpoints`). Both dispatch to a handler that gates on
        // `site:configure` against the route's siteId - the same shape every other write on a
        // `/sites/{siteId}/...` route already uses.
        group.MapPut("/{moduleKey}", HandleEnableAsync);
        group.MapDelete("/{moduleKey}", HandleDisableAsync);

        // `26-320`: the tenant admin's own edit of an already-enabled module's trigger words - the write
        // half `26-316` left out. A separate route from the enable `PUT` above, not a field folded onto its
        // body: enabling a module registers it (mints a credential, calls the module deployment, seeds
        // permissions), while this only re-words an existing row's Chat-side routing strings - two different
        // acts, the same "a separate route per distinct act, not one write that does several things" shape
        // the owner surface's own quantity/unconditional-grant routes already use (`OwnerModuleEndpoints`).
        // Gated on `site:configure` against the route's siteId by the handler, exactly like its siblings.
        group.MapPut("/{moduleKey}/trigger-words", HandleSetTriggerWordsAsync);
    }

    /// <summary>`23-01`: dispatches to <see cref="ListEnabledModulesForSiteHandler"/> rather than
    /// reading <c>IEnabledModuleReadStore</c> straight from the endpoint - see that handler's own
    /// remarks for why an endpoint-level read store call was the live cross-tenant hole this route
    /// group otherwise had, and why a handler is the shape every other read on this codebase's
    /// <c>/sites/{siteId}/...</c> routes already uses.</summary>
    private static async Task<IResult> HandleGetAsync(
        Guid siteId, ListEnabledModulesForSiteHandler handler, HttpContext httpContext, CancellationToken cancellationToken)
    {
        var result = await handler.HandleAsync(
            new ListEnabledModulesForSite(httpContext.User.GetOperatorId(), new SiteId(siteId)), cancellationToken);

        return result.IsFailure ? result.Error!.Value.ToProblem(httpContext) : Results.Ok(new EnabledModulesResponse(
            [.. result.Value.Select(m => new EnableModuleResponse(
                m.ModuleKey.Value, m.TriggerWords, m.EntryPoint.ToString(), m.GrantedByOwner, m.ExpiresAt))]));
    }

    /// <summary>`26-316`: the tenant admin's own enable - `PUT /api/v1/sites/{siteId}/modules/{moduleKey}`.
    /// The handler gates on `site:configure` against the route's siteId (never a body-supplied tenant),
    /// mints the per-site credential itself, and reads the entry point and provisioning secret from
    /// configuration - see <see cref="EnableModuleForSiteHandler"/>'s own remarks.</summary>
    private static async Task<IResult> HandleEnableAsync(
        Guid siteId, string moduleKey, EnableModuleRequest request, EnableModuleForSiteHandler handler,
        HttpContext httpContext, CancellationToken cancellationToken)
    {
        var result = await handler.HandleAsync(
            new EnableModuleForSite(
                httpContext.User.GetOperatorId(), new SiteId(siteId), moduleKey, request.TriggerWords),
            cancellationToken);

        return result.IsFailure
            ? result.Error!.Value.ToProblem(httpContext)
            : Results.Ok(new EnableModuleResponse(moduleKey, request.TriggerWords, EntryPoint: null));
    }

    /// <summary>`26-316`: the tenant admin's own disable - `DELETE /api/v1/sites/{siteId}/modules/{moduleKey}`.
    /// Non-destructive (stamps the row and deactivates the module-side registration; calendars and bookings
    /// survive), and refuses a platform-owner grant - see <see cref="DisableModuleForSiteHandler"/>.</summary>
    private static async Task<IResult> HandleDisableAsync(
        Guid siteId, string moduleKey, DisableModuleForSiteHandler handler, HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        var result = await handler.HandleAsync(
            new DisableModuleForSite(httpContext.User.GetOperatorId(), new SiteId(siteId), moduleKey),
            cancellationToken);

        return result.IsFailure ? result.Error!.Value.ToProblem(httpContext) : Results.Ok();
    }

    /// <summary>`26-320`: the tenant admin's own trigger-word edit -
    /// <c>PUT /api/v1/sites/{siteId}/modules/{moduleKey}/trigger-words</c>. The handler gates on
    /// `site:configure` against the route's siteId (never a body-supplied tenant), refuses an owner-granted
    /// module and a module this site does not have enabled, and reuses the owner path's own reserved-word
    /// and cross-module-collision validation - see <see cref="SetModuleTriggerWordsForSiteHandler"/>'s own
    /// remarks. Returns the replacement set on success, the same "echo what the caller set" shape
    /// <see cref="HandleEnableAsync"/> uses.</summary>
    private static async Task<IResult> HandleSetTriggerWordsAsync(
        Guid siteId, string moduleKey, SetTriggerWordsRequest request, SetModuleTriggerWordsForSiteHandler handler,
        HttpContext httpContext, CancellationToken cancellationToken)
    {
        var result = await handler.HandleAsync(
            new SetModuleTriggerWordsForSite(
                httpContext.User.GetOperatorId(), new SiteId(siteId), moduleKey, request.TriggerWords),
            cancellationToken);

        return result.IsFailure
            ? result.Error!.Value.ToProblem(httpContext)
            : Results.Ok(new SetTriggerWordsResponse(moduleKey, request.TriggerWords));
    }

    /// <summary>`26-316`: the body <c>PUT .../modules/{moduleKey}</c> takes - only the trigger words, the
    /// one thing <c>Ago.Chat.*</c> cannot know for the caller (it never learns what a module means -
    /// `ModuleKey`'s own remarks). No credential (the handler mints one), no entry point or provisioning
    /// secret (both from configuration), no expiry (a self-service grant never expires).</summary>
    public sealed record EnableModuleRequest(IReadOnlyList<string> TriggerWords);

    /// <param name="GrantedByOwner">`22-17`: <see langword="true"/> when the platform owner enabled this
    /// module rather than the tenant's own operator - the wire-visible half of that item's own audit
    /// distinction.</param>
    /// <param name="EntryPoint">`26-316`: <see langword="null"/> on the enable echo - a tenant enabling a
    /// module never supplied an entry point (it comes from configuration) and has no use for it back, the
    /// same "the response carries nothing the caller neither sent nor needs" hygiene the owner grant's own
    /// response already applies. Non-null only on the read (`GET`), which projects the stored value.</param>
    /// <param name="ExpiresAt"><see langword="null"/> for a grant that does not expire.</param>
    public sealed record EnableModuleResponse(
        string ModuleKey, IReadOnlyList<string> TriggerWords, string? EntryPoint, bool GrantedByOwner = false,
        DateTimeOffset? ExpiresAt = null);

    public sealed record EnabledModulesResponse(IReadOnlyList<EnableModuleResponse> Modules);

    /// <summary>`26-320`: the body <c>PUT .../modules/{moduleKey}/trigger-words</c> takes - the complete
    /// replacement set of trigger words, the same "the caller sends the whole list, this replaces not
    /// merges" shape <see cref="SetModuleTriggerWordsForSite"/>'s own remarks describe.</summary>
    public sealed record SetTriggerWordsRequest(IReadOnlyList<string> TriggerWords);

    /// <summary>`26-320`: echoes the words that were set - the same "confirm what the caller sent" hygiene
    /// <see cref="EnableModuleResponse"/> applies to the enable echo.</summary>
    public sealed record SetTriggerWordsResponse(string ModuleKey, IReadOnlyList<string> TriggerWords);
}
