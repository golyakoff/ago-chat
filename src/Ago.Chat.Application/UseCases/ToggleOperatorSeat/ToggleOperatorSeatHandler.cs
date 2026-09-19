using Ago.Chat.Application.Abstractions;
using Ago.Chat.Application.UseCases.OperatorRoleSeats;
using Ago.Chat.Domain;
using Ago.Platform.Kernel;

namespace Ago.Chat.Application.UseCases.ToggleOperatorSeat;

/// <summary>
/// `13-03`: a write against one operator's own role-seat pairing (<c>IOperatorRoleRepository.SetHoldsSeatAsync</c>) -
/// no outbox row, nothing else in this codebase reacts to a seat toggle by itself (contrast
/// <see cref="Operator.Remove"/>, which raises <see cref="OperatorRemoved"/> because a Worker consumer
/// genuinely needs to act on it).
///
/// <para><b>Toggling a seat on is capacity-checked against the role's own site limit; toggling one off
/// never is - an implementer's-call this item's own Scope left open ("up to the current seat_limit"),
/// decided here.</b> The over-seats condition (`13-03`'s own Scope) is real and is deliberately never
/// blocked when it arises from a downgrade (`decisions/0006`'s own rejection of blocking a downgrade on
/// live operator count) - but nothing in that decision says an owner should be able to manufacture a
/// *fresh* over-seats state by hand, one toggle at a time, when the role is not already over its limit.
/// Blocking a toggle-on that would push the held-seat count past the role's own limit mirrors
/// <see cref="OperatorRoleSeatCapacity"/>'s own reasoning for refusing a new invite/promotion for the
/// identical reason - "upgrade" is the real remedy, not "retry".</para>
///
/// <para><b>`25-170`: extended to the Admin role, and a real, previously-latent race closed along the
/// way.</b> Before this item, the capacity check (<c>held &gt;= site.SeatLimit</c>) and the actual
/// <c>Operator.ToggleSeat</c> write were two separate, unlocked statements - a genuine
/// check-then-act race this handler's own pre-`25-170` shape never protected against, because nothing
/// about toggling one account-level flag looked like it needed a transaction. Generalising the check to
/// share <see cref="OperatorRoleSeatCapacity"/>'s own row-locked primitive
/// (<see cref="IOperatorRoleRepository.LockAndGetHeldSeatHolderIdsAsync"/>) closes that race as a direct
/// consequence, not a separately-scoped fix: the lock and the write below now share one transaction, the
/// same way <c>OperatorInviteRedemptionRepository</c>'s own seat check always has.</para>
///
/// <para><b>`23-67`'s own last-manager finding still holds, restated for the role-scoped write.</b> This
/// handler still never checks "is this the last manager" - <see cref="Operator.CanSignIn"/> (`25-170`)
/// is "does any role this operator holds have `HoldsSeat = true`", and a non-removed holder of
/// <see cref="Permission.SiteManageOperators"/> via the Admin role who releases their <em>Operator</em>-role
/// seat still has their Admin-role seat untouched by this call, so they can still sign in either way -
/// the identical "this handler cannot move the one invariant `23-67` cares about" reasoning its own
/// pre-`25-170` remarks already gave, now also true of the newly-reachable "release the Admin-role seat
/// instead" case for the symmetric reason (their Operator-role seat, if any, is what this call leaves
/// untouched).</para>
/// </summary>
public sealed class ToggleOperatorSeatHandler(
    IOperatorRepository operators,
    IOperatorRoleRepository operatorRoles,
    IPermissionChecker permissions,
    IUnitOfWork unitOfWork,
    OperatorRoleSeatCapacity roleSeatCapacity)
{
    public async Task<Result> HandleAsync(ToggleOperatorSeat command, CancellationToken cancellationToken)
    {
        var allowed = await permissions.HasPermissionAsync(
            command.RequestedBy, command.SiteId, Permission.SiteManageOperators, cancellationToken);
        if (!allowed)
        {
            return ConversationErrors.Forbidden("Operator does not have permission to manage this site's operators.");
        }

        var target = await operators.GetByIdAsync(command.TargetOperatorId, command.SiteId, cancellationToken);
        if (target is null)
        {
            return ConversationErrors.OperatorNotFound(command.TargetOperatorId.Value);
        }

        if (!command.HoldsSeat)
        {
            // Never capacity-checked, and never needs the lock below - this handler's own class-level
            // remarks on why. A no-op if the operator never held this role's seat in the first place
            // (IOperatorRoleRepository.SetHoldsSeatAsync's own contract).
            await operatorRoles.SetHoldsSeatAsync(command.TargetOperatorId, command.SiteId, command.RoleName, holdsSeat: false, cancellationToken);
            return Result.Success();
        }

        var alreadyHeld = await operatorRoles.HoldsRoleSeatAsync(
            command.TargetOperatorId, command.SiteId, command.RoleName, cancellationToken);
        if (alreadyHeld)
        {
            // Already on - a harmless no-op, the same idempotent posture the pre-`25-170` handler
            // already gave this case.
            return Result.Success();
        }

        await using var transaction = await unitOfWork.BeginTransactionAsync(cancellationToken);

        var check = await roleSeatCapacity.CheckAsync(command.SiteId, command.RoleName, cancellationToken);
        if (check.IsAtCapacity)
        {
            // Disposed without a commit below - rolls back.
            return Refusal(command.RoleName, check.Limit);
        }

        await operatorRoles.SetHoldsSeatAsync(command.TargetOperatorId, command.SiteId, command.RoleName, holdsSeat: true, cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return Result.Success();
    }

    private static Error Refusal(string roleName, int limit) => roleName == RoleSeatLimits.AdminRoleName
        ? ConversationErrors.OperatorAdminLimitReached(limit)
        : ConversationErrors.OperatorSeatLimitReached(limit);
}
