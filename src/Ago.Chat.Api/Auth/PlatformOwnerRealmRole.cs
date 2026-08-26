using System.Security.Claims;
using System.Text.Json;

namespace Ago.Chat.Api.Auth;

/// <summary>
/// `12-04`: the single implementation of "does this validated token carry the `platform-owner` realm
/// role", extracted from <see cref="PlatformOwnerAuthorizationHandler"/> when a second surface needed
/// the same answer (<see cref="AuthorizationPolicies.NotThePlatformOwner"/>, which refuses `10-02`'s
/// registration bootstrap for that identity).
///
/// <para><b>Why extracted rather than re-derived.</b> `12-04` exists because three surfaces each
/// answered "what kind of principal is this token" for themselves and each answered differently
/// (`17-06`'s attachment route read it as a visitor, the console read it as a new registrant,
/// `12-01` means it as the platform owner). The lesson taken from that is not that the product needs
/// a central principal classifier - `adr/0063` records why it deliberately does not - but that the
/// one *recognition rule* this codebase does own must have exactly one implementation, so a second
/// copy cannot drift from the first. This class is that one implementation; every server-side caller
/// goes through it.</para>
///
/// <para>It reads a claim Keycloak itself signs and nothing else - no `operators` row, no `site_id`,
/// no <c>IPermissionChecker</c> - which is the structural half of the boundary `adr/0032` argues
/// for: no grant this codebase can write can change the answer.</para>
/// </summary>
internal static class PlatformOwnerRealmRole
{
    /// <summary>Keycloak's own claim name. Its value is a JSON *object* (`{"roles":[...]}`), which
    /// `JsonWebTokenHandler` surfaces as a single claim whose <see cref="Claim.Value"/> is the raw
    /// JSON text - hence the parse below rather than a plain `RequireClaim`, which can only compare
    /// whole claim values and would therefore have to match a serialized JSON blob exactly.</summary>
    internal const string RealmAccessClaimType = "realm_access";

    private const string RolesPropertyName = "roles";

    /// <summary>Every `realm_access` claim on the principal is considered, not just the first: a
    /// principal can carry more than one <see cref="ClaimsIdentity"/> (this project adds one in
    /// <see cref="OperatorIdentityClaimsTransformation"/>), and picking "the first" would make the
    /// answer depend on identity ordering. Only a claim Keycloak signed can contain the role name, so
    /// scanning all of them widens nothing.
    ///
    /// <para>Returns <c>false</c> for an unauthenticated or absent principal. Both callers want that
    /// reading, for opposite reasons that happen to agree: the owner policy must deny an anonymous
    /// caller, and the registration policy's own <c>RequireAuthenticatedUser</c> has already rejected
    /// one before this is consulted.</para></summary>
    public static bool IsHeldBy(ClaimsPrincipal? user)
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
