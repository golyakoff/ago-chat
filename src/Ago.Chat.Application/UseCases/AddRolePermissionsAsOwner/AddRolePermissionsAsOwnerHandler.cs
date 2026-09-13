using Ago.Chat.Application.Abstractions;
using Ago.Chat.Domain;
using Ago.Platform.Kernel;

namespace Ago.Chat.Application.UseCases.AddRolePermissionsAsOwner;

/// <summary>
/// `25-76`: validates, then delegates entirely to
/// <see cref="Application.Abstractions.IRoleRepository.AddPermissionsAsync"/> - see
/// <see cref="AddRolePermissionsAsOwner"/>'s own remarks for why this handler owns no write logic of
/// its own.
///
/// <para><b>No <see cref="IPermissionChecker"/> call, the identical reason every other owner surface in
/// this codebase gives.</b> The fact that authorizes this call - the <c>RequirePlatformOwner</c> policy
/// on the route that resolves this handler - does not live in a table <see cref="IPermissionChecker"/>
/// could check, so a permission check here would be a second, weaker copy of a decision the policy
/// already made (<see cref="SetUnconditionalModuleGrantAsOwner.SetUnconditionalModuleGrantAsOwnerHandler"/>'s
/// own remarks, unchanged).</para>
///
/// <para><b>Validation order: shape, then vocabulary, then existence.</b> An empty list is refused
/// before anything is looked up (nothing to validate against a role that might not exist); every
/// submitted value is checked against the closed <see cref="Domain.Permission.AllKnownValues"/>
/// vocabulary next, so a typo is reported as "not a real permission" rather than as a confusing
/// "role not found" once role resolution fails for an unrelated reason; only then is the role itself
/// resolved, so the honest 400 (a role name that does not exist on this site) is never masked by an
/// earlier, unrelated failure.</para>
/// </summary>
public sealed class AddRolePermissionsAsOwnerHandler(IRoleRepository roles)
{
    public async Task<Result> HandleAsync(AddRolePermissionsAsOwner command, CancellationToken cancellationToken)
    {
        if (command.Permissions.Count == 0)
        {
            return ConversationErrors.RolePermissionsRequired(
                "At least one permission must be given to add.");
        }

        var distinctPermissions = command.Permissions.Distinct(StringComparer.Ordinal).ToArray();

        var unknown = distinctPermissions
            .Where(p => !Permission.AllKnownValues.Contains(p, StringComparer.Ordinal))
            .ToArray();
        if (unknown.Length > 0)
        {
            return ConversationErrors.RolePermissionUnknown(
                $"Not a real permission: {string.Join(", ", unknown)}.");
        }

        var role = await roles.GetByNameAsync(command.SiteId, command.RoleName, cancellationToken);
        if (role is null)
        {
            return ConversationErrors.OperatorRoleNotFound(
                $"Site {command.SiteId.Value} has no role named '{command.RoleName}'.");
        }

        await roles.AddPermissionsAsync(command.SiteId, command.RoleName, distinctPermissions, cancellationToken);

        return Result.Success();
    }
}
