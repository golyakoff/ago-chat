using Ago.Chat.Application.Abstractions;
using Ago.Chat.Domain;

namespace Ago.Chat.Application.UseCases.OperatorRoleSeats;

/// <summary>
/// `25-170`: "one capacity-check procedure, parameterized by role name and the `Site` field that limits
/// it" (this item's own design) - replacing both `OperatorInviteRedemptionRepository`'s own hand-written
/// Operator-role seat count and `ChangeOperatorRoleHandler`'s own separately hand-written Admin-role
/// count. Both existing call sites move to this; no new capacity rule is invented, the two existing ones
/// are unified.
///
/// <para><b>An Application class composing one port, not a port of its own.</b> The identical judgement
/// <see cref="Ago.Chat.Application.UseCases.AiAddOn.AiProcessingGate"/>'s own remarks make for itself:
/// this touches no external resource of its own - it calls
/// <see cref="IOperatorRoleRepository.LockAndGetHeldSeatHolderIdsAsync"/> and
/// <see cref="ISiteRepository.GetByIdAsync"/>, two ports each caller already has or gains by
/// constructor injection - so a third port here would only hide the two-step resolution (lock-and-count,
/// then compare against the role's own limit) a reviewer needs to see to trust the decision.</para>
///
/// <para><b>Must be called inside the caller's own ambient transaction</b> - the identical contract
/// <see cref="IOperatorRoleRepository.LockAndGetHeldSeatHolderIdsAsync"/> itself states, since the whole
/// point is that the row lock this method takes stays held until the caller's own write (an invite
/// redemption, a role change, a seat toggle) either commits or rolls back with it.</para>
/// </summary>
public sealed class OperatorRoleSeatCapacity(IOperatorRoleRepository operatorRoles, ISiteRepository sites)
{
    public async Task<RoleSeatCapacityCheck> CheckAsync(SiteId siteId, string roleName, CancellationToken cancellationToken)
    {
        // Locks the site row first, then reads its own limit off the same (now-locked) row - the
        // identical order `OperatorInviteRedemptionRepository`'s own pre-`25-170` shape already used
        // (count/lock first, read the limit second), so a caller's own subsequent plain read of `Site`
        // sees the same current, locked value.
        var holderIds = await operatorRoles.LockAndGetHeldSeatHolderIdsAsync(siteId, roleName, cancellationToken);

        var site = await sites.GetByIdAsync(siteId, cancellationToken);
        if (site is null)
        {
            // A foreign key (OperatorRoleRecordConfiguration/RoleRecordConfiguration.HasOne<Site>)
            // should make this unreachable - the same "should have prevented this" throw this
            // codebase's other site-row locks already raise for the identical impossible case.
            throw new InvalidOperationException(
                $"Site {siteId.Value} was not found while checking role-seat capacity for role '{roleName}' - "
                + "a foreign key should have prevented this.");
        }

        var limit = RoleSeatLimits.LimitFor(roleName, site);
        return new RoleSeatCapacityCheck(holderIds.Count >= limit, limit);
    }
}

/// <summary>One capacity check's own outcome - <see cref="Limit"/> travels with
/// <see cref="IsAtCapacity"/> so every caller can build its own existing refusal shape
/// (<c>ConversationErrors.OperatorSeatLimitReached</c>/<c>OperatorAdminLimitReached</c>,
/// <c>OperatorInviteRedemptionResult.SeatLimitReached</c>/<c>AdminLimitReached</c>) without a second
/// read.</summary>
public sealed record RoleSeatCapacityCheck(bool IsAtCapacity, int Limit);
