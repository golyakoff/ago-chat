using Ago.Chat.Application.Abstractions;
using Ago.Chat.Domain;
using Ago.Platform.Kernel;

namespace Ago.Chat.Application.UseCases.RemoveRolePermissionsAsOwner;

/// <summary>
/// `25-77`: validates, then delegates entirely to
/// <see cref="Application.Abstractions.IRoleRepository.RemovePermissionsAsync"/> - the identical
/// "this handler owns no write logic of its own" shape
/// <see cref="AddRolePermissionsAsOwner.AddRolePermissionsAsOwnerHandler"/> already establishes for the
/// grant direction.
///
/// <para><b>No <see cref="IPermissionChecker"/> call</b> - the identical reason every other owner
/// surface in this codebase gives: the fact that authorizes this call is the <c>RequirePlatformOwner</c>
/// policy on the route that resolves this handler, which <see cref="IPermissionChecker"/> could not see
/// even if asked.</para>
///
/// <para><b>Validation order: shape, then vocabulary, then existence</b> - the identical order
/// <see cref="AddRolePermissionsAsOwner.AddRolePermissionsAsOwnerHandler"/>'s own remarks state, with
/// one addition ahead of all three: the reason is checked first, unconditionally, before the permission
/// list is even inspected - the identical "reject the caller's own input before touching anything else"
/// ordering <see cref="SetUnconditionalModuleGrantAsOwner.SetUnconditionalModuleGrantAsOwnerHandler"/>'s
/// own reason guard already follows for the analogous act on a different aggregate.</para>
///
/// <para><b>The known-vocabulary check still runs on the removal side</b>, even though a value that was
/// never a real permission could only ever be a harmless no-op against `roles` (it was never present to
/// remove). Kept anyway, for the same reason `AddRolePermissionsAsOwnerHandler`'s own check exists on
/// the grant side: an owner who mistypes `stie:configure` when trying to remove `site:configure` deserves
/// "that is not a real permission" back, not a silent `200 OK` that changed nothing and leaves them
/// believing the removal took effect. Refusing the typo before it ever reaches the database is strictly
/// more honest than a quiet no-op would be.</para>
/// </summary>
public sealed class RemoveRolePermissionsAsOwnerHandler(IRoleRepository roles)
{
    /// <summary><see cref="RevokeModuleForSiteAsOwner.RevokeModuleForSiteAsOwnerHandler.MaxReasonLength"/>
    /// reused rather than reinvented - the identical shape of thing (a person typing a free-text
    /// justification for an owner-only override), the same precedent
    /// <see cref="SetUnconditionalModuleGrantAsOwner.SetUnconditionalModuleGrantAsOwnerHandler.MaxReasonLength"/>
    /// already reuses it for a third, unrelated override.</summary>
    public const int MaxReasonLength = RevokeModuleForSiteAsOwner.RevokeModuleForSiteAsOwnerHandler.MaxReasonLength;

    public async Task<Result> HandleAsync(RemoveRolePermissionsAsOwner command, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(command.Reason))
        {
            return ConversationErrors.RolePermissionRemovalReasonRequired(
                "A reason is required whenever a permission is removed from a role - state why.");
        }

        if (command.Reason.Trim().Length > MaxReasonLength)
        {
            return ConversationErrors.RolePermissionRemovalReasonRequired(
                $"A permission-removal reason cannot exceed {MaxReasonLength} characters.");
        }

        if (command.Permissions.Count == 0)
        {
            return ConversationErrors.RolePermissionsRequired(
                "At least one permission must be given to remove.");
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

        // `25-77`'s own "no magic roles" - no check here against role.Permissions asking whether any
        // named permission "matters" to the role's own identity. Every permission Permission.cs names
        // is equally removable, up to and including a role's defining permissions; the port itself
        // already treats removing an absent permission as a harmless no-op, so nothing here needs to
        // pre-filter the list either.
        await roles.RemovePermissionsAsync(
            command.SiteId, command.RoleName, distinctPermissions, command.RemovedBy, command.Reason.Trim(),
            cancellationToken);

        return Result.Success();
    }
}
