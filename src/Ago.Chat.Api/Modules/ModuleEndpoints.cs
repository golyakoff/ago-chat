using Ago.Chat.Api.Auth;
using Ago.Chat.Api.Http;
using Ago.Chat.Application.UseCases.ListEnabledModulesForSite;
using Ago.Chat.Domain;

namespace Ago.Chat.Api.Modules;

/// <summary>
/// `19-03`/`22-11` built a tenant's own self-service write surface here: register a module, rotate
/// its credential, revoke it, verify its registration. `23-83`/`adr/0151` removes all four, not
/// re-plumbed - only the read stays.
///
/// <para><b>Why the writes are gone rather than repaired.</b> `19-03`'s own original comment recorded
/// the mistake without knowing it was one: <i>"this item needed a real console screen to register the
/// FAQ module for a site, so this is that endpoint."</i> That welded two incompatible ideas together -
/// a tenant turning a product on for themselves, and turning a product on meaning proving to the
/// module that the *platform* authorised it (`adr/0095`'s deployment-wide
/// <see cref="Domain.ModuleProvisioningSecret"/>). Together they required a tenant operator, the
/// least trusted caller in this system, to hold a secret that works against every other tenant the
/// deployment serves - `23-65`/`adr/0150` had already taken that same secret out of the platform
/// owner's own hands (who could read it from the cluster anyway) while this route kept demanding it
/// from someone who never could. `adr/0151`'s own answer: a tenant never turns a capability on for
/// themselves - the platform does, or the system does on a payment - so the fix is not making the
/// field reachable from configuration the way the owner's routes now are; it is removing the write
/// entirely. `23-84` found that the console's own form never sent the required fields in the first
/// place, so this capability is establishedly not one any tenant could ever have exercised.</para>
///
/// <para><b>What replaces them.</b> Enabling and revoking a module already had a platform-owner
/// counterpart (<see cref="Api.Owner.OwnerModuleEndpoints"/>, `22-17`). Rotating a credential and
/// verifying a registration did not - `23-83` adds them there
/// (<see cref="Application.UseCases.RotateModuleCredentialAsOwner.RotateModuleCredentialAsOwnerHandler"/>,
/// <see cref="Application.UseCases.VerifyModuleRegistrationAsOwner.VerifyModuleRegistrationAsOwnerHandler"/>),
/// so nothing that was genuinely possible before this item becomes impossible after it - it becomes
/// the platform's own act instead of a tenant's.</para>
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

    /// <param name="GrantedByOwner">`22-17`: <see langword="true"/> when the platform owner enabled this
    /// module rather than the tenant's own operator - the wire-visible half of that item's own audit
    /// distinction.</param>
    /// <param name="ExpiresAt"><see langword="null"/> for a grant that does not expire.</param>
    public sealed record EnableModuleResponse(
        string ModuleKey, IReadOnlyList<string> TriggerWords, string EntryPoint, bool GrantedByOwner = false,
        DateTimeOffset? ExpiresAt = null);

    public sealed record EnabledModulesResponse(IReadOnlyList<EnableModuleResponse> Modules);
}
