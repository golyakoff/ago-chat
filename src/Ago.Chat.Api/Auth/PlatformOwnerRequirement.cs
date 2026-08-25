using Microsoft.AspNetCore.Authorization;

namespace Ago.Chat.Api.Auth;

/// <summary>
/// `12-01`/`adr/0032`: the platform owner - the operator of the service itself, not of any one
/// tenant. Deliberately *not* expressible in `adr/0016`'s RBAC model: a `Role` there is tenant-local
/// (scoped to exactly one `site_id`), so "every site" has no representation in it that would not
/// first require weakening the invariant that model exists to hold. This requirement is satisfied by
/// a Keycloak *realm* role carried on the validated token instead - a fact about the identity
/// provider's own assignment, not about anything in this project's `roles`/`operator_roles` tables.
///
/// <para><see cref="RealmRoleName"/> is a compile-time constant on purpose, not configuration. A
/// configurable role name would introduce a "key missing or empty" state whose only safe reading is
/// "deny everyone" - a fail-closed branch that has to be written correctly, and that a future edit
/// could get wrong once (e.g. an empty configured value matching an empty role string). A constant
/// has no such state: there is nothing to omit, nothing to leave blank, and nothing an operator of
/// the deployment can accidentally widen. Revocation stays where `adr/0032` puts it - Keycloak's
/// admin console, removing the role assignment - which needs no code change and no redeploy, so
/// configurability buys nothing here that it costs a failure mode for.</para>
/// </summary>
internal sealed class PlatformOwnerRequirement : IAuthorizationRequirement
{
    /// <summary>The Keycloak realm-role name. Must match the `platform-owner` realm role defined in
    /// every realm-import file this project maintains (`ago-deploy/k8s/base/keycloak-realm-import.json`
    /// and `tests/Ago.Chat.Integration.Tests/keycloak-realm-import.json`). Who *holds* it is never
    /// committed anywhere - `adr/0032`, `repositories.md`'s "no secrets, ever."</summary>
    public const string RealmRoleName = "platform-owner";
}
