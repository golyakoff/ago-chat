using Ago.Chat.Application.Abstractions;
using Ago.Chat.Domain;
using Ago.Platform.Kernel;

namespace Ago.Chat.Application.UseCases.SetUnconditionalModuleGrantAsOwner;

/// <summary>
/// `23-86`: sets or lifts the platform owner's own unconditional-grant flag for one (site, module) -
/// see <see cref="Domain.ModuleQuantityGrant"/>'s own remarks for what the flag does and does not
/// change. Never asks the module anything and never touches
/// <see cref="Domain.ModuleQuantityGrant.Quantity"/> directly - the identical "chat only makes the
/// number durable and tells the module it changed" posture
/// <see cref="GrantModuleQuantityAsOwner.GrantModuleQuantityAsOwnerHandler"/>'s own remarks state,
/// restated here for the flag rather than the quantity itself.
///
/// <para><b>No <see cref="IPermissionChecker"/> call, for the identical reason every other owner
/// surface in this codebase gives.</b> The fact that authorizes this call - the
/// <c>RequirePlatformOwner</c> policy on the route that resolves this handler - does not live in a
/// table <see cref="IPermissionChecker"/> could check, so a permission check here would be a second,
/// weaker copy of a decision the policy already made (<see cref="EnableModuleForSiteAsOwner.EnableModuleForSiteAsOwnerHandler"/>'s
/// own remarks, unchanged).</para>
///
/// <para><b>The reason check runs first, unconditionally, before this handler ever touches the store</b> -
/// the identical "reject the caller's input before touching another system" ordering
/// <see cref="RevokeModuleForSiteAsOwner.RevokeModuleForSiteAsOwnerHandler"/>'s own force/reason guard
/// already follows, restated here since both directions of this write (set and lift) require a
/// reason.</para>
/// </summary>
public sealed class SetUnconditionalModuleGrantAsOwnerHandler(IModuleQuantityGrantStore grants, IClock clock)
{
    /// <summary><see cref="RevokeModuleForSiteAsOwner.RevokeModuleForSiteAsOwnerHandler.MaxReasonLength"/>
    /// reused rather than reinvented - the identical shape of thing (a person typing a free-text
    /// justification for an owner-only override), the same precedent
    /// <see cref="RestoreOperatorSeatAsOwner.RestoreOperatorSeatAsOwnerHandler.MaxReasonLength"/>
    /// already reuses it for a third, unrelated override.</summary>
    public const int MaxReasonLength = RevokeModuleForSiteAsOwner.RevokeModuleForSiteAsOwnerHandler.MaxReasonLength;

    public async Task<Result> HandleAsync(SetUnconditionalModuleGrantAsOwner command, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(command.Reason))
        {
            return ConversationErrors.ModuleQuantityUnconditionalGrantReasonRequired(
                "A reason is required whenever the unconditional-grant flag is set or lifted - state why.");
        }

        if (command.Reason.Trim().Length > MaxReasonLength)
        {
            return ConversationErrors.ModuleQuantityUnconditionalGrantReasonRequired(
                $"An unconditional-grant reason cannot exceed {MaxReasonLength} characters.");
        }

        ModuleKey moduleKey;
        try
        {
            moduleKey = new ModuleKey(command.ModuleKey);
        }
        catch (ArgumentException ex)
        {
            return ConversationErrors.ModuleInvalid(ex.Message);
        }

        await grants.SetUnconditionalGrantAsync(
            command.SiteId, moduleKey, command.UnconditionallyGranted, command.SetBy, command.Reason.Trim(),
            clock.UtcNow, cancellationToken, command.ExpiresAt);

        return Result.Success();
    }
}
