using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;

namespace Ago.Chat.Api.Auth;

/// <summary>
/// `12-01`/`adr/0032`: decides <see cref="PlatformOwnerRequirement"/> by reading Keycloak's
/// `realm_access.roles` claim off an already-validated token, and nothing else.
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
    /// <summary>Keycloak's own claim name. Its value is a JSON *object* (`{"roles":[...]}`), which
    /// `JsonWebTokenHandler` surfaces as a single claim whose <see cref="Claim.Value"/> is the raw
    /// JSON text - hence the parse below rather than a plain `RequireClaim`, which can only compare
    /// whole claim values and would therefore have to match a serialized JSON blob exactly.</summary>
    internal const string RealmAccessClaimType = "realm_access";

    private const string RolesPropertyName = "roles";

    protected override Task HandleRequirementAsync(
        AuthorizationHandlerContext context, PlatformOwnerRequirement requirement)
    {
        if (HoldsOwnerRealmRole(context.User))
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

    /// <summary>Every `realm_access` claim on the principal is considered, not just the first: a
    /// principal can carry more than one <see cref="ClaimsIdentity"/> (this project adds one in
    /// `OperatorIdentityClaimsTransformation`), and picking "the first" would make the answer depend
    /// on identity ordering. Only a claim Keycloak signed can contain the role name, so scanning all
    /// of them widens nothing.</summary>
    private static bool HoldsOwnerRealmRole(ClaimsPrincipal? user)
    {
        if (user?.Identity is not { IsAuthenticated: true })
        {
            return false;
        }

        foreach (var claim in user.FindAll(RealmAccessClaimType))
        {
            if (ContainsOwnerRole(claim.Value))
            {
                return true;
            }
        }

        return false;
    }

    private static bool ContainsOwnerRole(string realmAccessJson)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(realmAccessJson);
        }
        catch (JsonException)
        {
            // A claim that is not JSON at all cannot be Keycloak's realm_access object. Denying is
            // the only reading of it that is safe; throwing would turn a hostile token into a 500.
            return false;
        }

        using (document)
        {
            if (document.RootElement.ValueKind is not JsonValueKind.Object
                || !document.RootElement.TryGetProperty(RolesPropertyName, out var roles)
                || roles.ValueKind is not JsonValueKind.Array)
            {
                return false;
            }

            foreach (var role in roles.EnumerateArray())
            {
                // Ordinal, case-sensitive: Keycloak role names are case-sensitive, and a
                // culture-aware or case-insensitive comparison would accept a role this project's
                // realm-import files never define.
                if (role.ValueKind is JsonValueKind.String
                    && string.Equals(role.GetString(), PlatformOwnerRequirement.RealmRoleName, StringComparison.Ordinal))
                {
                    return true;
                }
            }
        }

        return false;
    }
}
