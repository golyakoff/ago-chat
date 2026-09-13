using Ago.Chat.Domain;

namespace Ago.Chat.Application.Abstractions;

/// <summary>
/// `13-01`: the one real caller `RoleRecord`'s own remarks anticipated ("nothing above
/// `PermissionChecker` manages roles yet, so there is nothing for a richer model to buy") - resolving
/// the site-local role an invite grants, by the fixed name the inviting operator names
/// (`"Operator"`/`"Admin"`), to the `roles` row id `operator_roles` actually points at. Shaped around
/// that one question, not a general role-management port - `authorization.md` already defers "who can
/// grant a role" past the seed script for a reason this item does not revisit.
///
/// <para><b>`23-102` adds a second question: "this role now also carries these permissions", never
/// "who holds this role".</b> <see cref="AddPermissionsAsync"/> grows the vocabulary a role's holders may
/// exercise - the same act <see cref="UseCases.RegisterSite.RegisterSiteHandler"/> performs once, at
/// registration - performed again, additively, when a module grant needs a permission the role does not
/// yet carry. It never touches `operator_roles` (which operator holds which role stays entirely the
/// tenant's, `23-72`'s own surface, untouched here).</para>
/// </summary>
public interface IRoleRepository
{
    /// <summary><see langword="null"/> when this site has no role by that name - `5-08`'s two seeded
    /// roles (`"Operator"`, `"Admin"`) are the only names any site has today, but this method makes no
    /// assumption about the set being exactly those two; a caller passing an unrecognised name gets a
    /// miss, not a guess.</summary>
    Task<Guid?> GetIdByNameAsync(SiteId siteId, string name, CancellationToken cancellationToken);

    /// <summary>
    /// `23-102`: adds <paramref name="permissions"/> to the named role's own permission set, idempotently
    /// - granting the same permission twice (a repeated module grant, a revoke-then-re-grant cycle) is a
    /// no-op the second time, never a duplicate entry and never an error. A no-op (not a miss) when this
    /// site has no role by that name, or when <paramref name="permissions"/> is empty - the same "nothing
    /// to do" shape <see cref="IModulePermissionsProvider"/>'s own <c>ModulePermissionSet.Empty</c>
    /// already represents one level up, so a module that needs nothing extra costs this method nothing to
    /// call.
    ///
    /// <para><b>`23-104`: also publishes.</b> A permission change is worthless to whichever product reads
    /// permissions from a replicated projection (`adr/0093`) until that projection learns it, so this
    /// method enqueues one <c>RoleAssignmentsChanged</c> - through the same outbox every other publisher
    /// of that event uses, in the same transaction as the permission change itself (rule 4) - for every
    /// operator currently holding <paramref name="roleName"/> on this site who has a linked external
    /// identity, not only whichever operator's request happened to trigger this call. An operator with no
    /// linked identity yet has no projection row anywhere to correct, so they are skipped, not thrown for
    /// - the implementation's own remarks give the fuller reasoning, including how this stays correct
    /// against a concurrent operator removal.</para>
    /// </summary>
    Task AddPermissionsAsync(SiteId siteId, string roleName, IReadOnlyCollection<string> permissions, CancellationToken cancellationToken);

    /// <summary>
    /// `23-72`: `ChangeOperatorRoleHandler`'s own lookup - it needs both the id (to write the new
    /// `operator_roles` row) and the permission set (to test whether the named role grants
    /// `site:manage_operators`, and to publish the resulting `RoleAssignmentsChanged` fact) for the same
    /// role in the same request, so one method returns both rather than making the caller round-trip
    /// twice. Deliberately a second method rather than widening <see cref="GetIdByNameAsync"/>'s own
    /// return shape - that method has one existing caller (`CreateOperatorInviteHandler`) that only ever
    /// needed the id, and changing its return type for a second caller's needs would be exactly the kind
    /// of ripple this port's own growth policy (`IRoleRepository`'s own remarks: "grow this only when a
    /// second real caller needs a different question answered") means to avoid paying when an additive
    /// method costs nothing.
    /// </summary>
    Task<RoleLookup?> GetByNameAsync(SiteId siteId, string name, CancellationToken cancellationToken);

    /// <summary>
    /// `25-76`: every role this site has, with its own actual, current permission list - not the
    /// founder template <see cref="UseCases.RegisterSite.RegisterSiteHandler"/> happened to write at
    /// registration, what this specific row holds today (this item's own "Found": seven live tenants,
    /// seven different `Admin` permission sets, because nothing before this item ever re-read one).
    /// The owner's site-detail screen is the one caller, and it always wants every role on the site at
    /// once - a single query over `roles` filtered by <c>site_id</c>, not <see cref="GetByNameAsync"/>
    /// called once per fixed name, so a site is one round trip to describe fully regardless of how many
    /// roles it happens to have (still always exactly the two seeded names today, `IRoleRepository`'s
    /// own remarks on `GetIdByNameAsync`, but this method makes no assumption about that either).
    /// </summary>
    Task<IReadOnlyList<RoleSummary>> GetAllForSiteAsync(SiteId siteId, CancellationToken cancellationToken);

    /// <summary>
    /// `25-77`: <see cref="AddPermissionsAsync"/>'s mirror - removes <paramref name="permissions"/> from
    /// the named role's own permission set, idempotently in reverse: removing an already-absent
    /// permission is a no-op, never an error and never a miss. A no-op (not a miss) when this site has
    /// no role by that name, or when <paramref name="permissions"/> is empty - the identical "nothing to
    /// do" shape <see cref="AddPermissionsAsync"/>'s own remarks state for the grant direction.
    ///
    /// <para><b>No magic roles.</b> <c>docs/backlog/25-77-*.md</c>'s own "Answered, 2026-09-13": the
    /// platform owner may remove any permission from any role, `Admin`'s own defining
    /// `site:configure`/`site:manage_operators` included - the owner is trusted, and this method carries
    /// no allow/deny list narrowing what it will remove. A role can be reduced to holding no permissions
    /// at all; that is a deliberate, reachable state, not a bug this method guards against.</para>
    ///
    /// <para><b>Publishes exactly like the grant direction</b> - one <c>RoleAssignmentsChanged</c>, in
    /// the same transaction as the permission change (rule 4), for every operator currently holding
    /// <paramref name="roleName"/> with a linked external identity. See <see cref="AddPermissionsAsync"/>'s
    /// own remarks for the full reasoning this direction reuses unchanged: why the publish is keyed by
    /// role rather than by caller, why an operator with no linked identity is skipped rather than thrown
    /// for, and how this stays correct against a concurrent operator removal.</para>
    ///
    /// <para><b>The one real signature difference from the grant direction: <paramref name="removedBy"/>
    /// and <paramref name="reason"/> are required, non-optional parameters.</b> `25-77`'s own "Answered":
    /// a reason is required on every removal, the identical discipline `adr/0118`'s forced-revoke and
    /// `23-86`'s unconditional-grant flag already require for taking something away from a tenant that it
    /// already had - never assumed, never defaulted. Unlike those two precedents (a second write, on a
    /// separate connection, after the domain write already committed), this method records the
    /// <paramref name="reason"/> - one row in `role_permission_removal_overrides` - inside its own
    /// already-open transaction, alongside the `roles` `UPDATE` and the outbox publish: the
    /// implementation's own remarks state why that is possible here where it was not for
    /// <c>ModuleRevokeOverrideRepository</c>'s own raw-Npgsql shape.</para>
    /// </summary>
    Task RemovePermissionsAsync(
        SiteId siteId, string roleName, IReadOnlyCollection<string> permissions, string removedBy, string reason,
        CancellationToken cancellationToken);
}

/// <summary>One role, resolved by name for <see cref="IRoleRepository.GetByNameAsync"/> - a plain
/// projection of the `roles` row, not a Domain model (roles have none yet, `OperatorInvite.RoleId`'s own
/// remarks).</summary>
public sealed record RoleLookup(Guid Id, IReadOnlyList<string> Permissions);

/// <summary>One row of <see cref="IRoleRepository.GetAllForSiteAsync"/> - the identical two facts
/// <see cref="RoleLookup"/> already carries for a role resolved by name, plus <see cref="Name"/> itself,
/// since a caller reading every role on a site (rather than one it already named) has no other way to
/// tell the rows apart.</summary>
public sealed record RoleSummary(string Name, Guid Id, IReadOnlyList<string> Permissions);
