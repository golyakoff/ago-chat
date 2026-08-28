using Ago.Chat.Domain;

namespace Ago.Chat.Application.Abstractions;

/// <summary>
/// `13-01`: the one real caller `RoleRecord`'s own remarks anticipated ("nothing above
/// `PermissionChecker` manages roles yet, so there is nothing for a richer model to buy") - resolving
/// the site-local role an invite grants, by the fixed name the inviting operator names
/// (`"Operator"`/`"Admin"`), to the `roles` row id `operator_roles` actually points at. Shaped around
/// that one question, not a general role-management port - `authorization.md` already defers "who can
/// grant a role" past the seed script for a reason this item does not revisit.
/// </summary>
public interface IRoleRepository
{
    /// <summary><see langword="null"/> when this site has no role by that name - `5-08`'s two seeded
    /// roles (`"Operator"`, `"Admin"`) are the only names any site has today, but this method makes no
    /// assumption about the set being exactly those two; a caller passing an unrecognised name gets a
    /// miss, not a guess.</summary>
    Task<Guid?> GetIdByNameAsync(SiteId siteId, string name, CancellationToken cancellationToken);
}
