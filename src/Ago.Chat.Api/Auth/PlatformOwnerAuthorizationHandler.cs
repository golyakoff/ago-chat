using Microsoft.AspNetCore.Authorization;

namespace Ago.Chat.Api.Auth;

/// <summary>
/// `12-01`/`adr/0032`: decides <see cref="PlatformOwnerRequirement"/> by reading Keycloak's
/// `realm_access.roles` claim off an already-validated token, and nothing else. The reading itself
/// lives in <see cref="PlatformOwnerRealmRole"/> since `12-04` gave it a second caller; this handler
/// is the policy-layer adapter over that one rule.
///
/// <para><b>What this handler deliberately does not touch.</b> It never resolves an
/// <see cref="AgoClaimTypes.OperatorId"/> or <see cref="AgoClaimTypes.SiteId"/> claim, never queries
/// the `operators`/`roles`/`operator_roles` tables, and never consults `IPermissionChecker`. That is
/// the structural half of the boundary `adr/0032` argues for: no grant this codebase can write -
/// including `5-08`'s site-wide `"Admin"` role holding `site:configure` - can make this handler
/// succeed, because it reads no value any of those writes can reach. The only input is a claim
/// Keycloak itself signs, and the only way to obtain it is a realm-role assignment in Keycloak's own
/// admin console. `OperatorIdentityClaimsTransformation` still runs (it is a global
/// `IClaimsTransformation`, not something a policy opts into), but whatever it adds or fails to add
/// is irrelevant here - a platform owner needs no `operators` row at all.</para>
///
/// <para><b>Fail-closed.</b> Every path that is not "a `realm_access` claim parses as an object whose
/// `roles` array contains exactly <see cref="PlatformOwnerRequirement.RealmRoleName"/> as a string"
/// calls <see cref="AuthorizationHandlerContext.Fail()"/>. Not merely "does not succeed": an explicit
/// `Fail` is sticky for the whole policy evaluation, so a second handler registered for this same
/// requirement later - by accident or by a well-meaning refactor - cannot grant owner access this
/// handler denied. A missing claim, a malformed claim, a `roles` value that is not an array, or a
/// role name that differs by so much as case all land there.</para>
/// </summary>
internal sealed class PlatformOwnerAuthorizationHandler : AuthorizationHandler<PlatformOwnerRequirement>
{
    protected override Task HandleRequirementAsync(
        AuthorizationHandlerContext context, PlatformOwnerRequirement requirement)
    {
        if (PlatformOwnerRealmRole.IsHeldBy(context.User))
        {
            context.Succeed(requirement);
        }
        else
        {
            context.Fail(new AuthorizationFailureReason(
                this, $"The token carries no '{PlatformOwnerRequirement.RealmRoleName}' realm role."));
        }

        return Task.CompletedTask;
    }
}
