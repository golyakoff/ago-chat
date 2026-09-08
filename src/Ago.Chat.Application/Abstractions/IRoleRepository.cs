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
}
