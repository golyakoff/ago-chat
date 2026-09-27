using Ago.Chat.Application.Abstractions;
using Ago.Chat.Domain;
using Ago.Platform.Kernel;

namespace Ago.Chat.Application.UseCases.OperatorRoleSeats;

/// <summary>
/// `25-170`: "one capacity-check procedure, parameterized by role name and the `Site` field that limits
/// it" (this item's own design) - replacing both `OperatorInviteRedemptionRepository`'s own hand-written
/// Operator-role seat count and `ChangeOperatorRoleHandler`'s own separately hand-written Admin-role
/// count. Both existing call sites move to this; no new capacity rule is invented, the two existing ones
/// are unified.
///
/// <para><b>An Application class composing two ports, not a port of its own.</b> The identical judgement
/// <see cref="Ago.Chat.Application.UseCases.AiAddOn.AiProcessingGate"/>'s own remarks make for itself:
/// this touches no external resource of its own - it calls
/// <see cref="IOperatorRoleRepository.LockAndGetHeldSeatHolderIdsAsync"/>,
/// <see cref="ISiteRepository.GetByIdAsync"/> and (`25-181`) <see cref="IOwnerSeatGrantStore.GetEffectiveExtraAsync"/>,
/// ports every caller already has or gains by constructor injection - so a fourth port here would only
/// hide the resolution (lock-and-count, then compare against the role's own limit plus whatever the
/// owner has hand-granted) a reviewer needs to see to trust the decision.</para>
///
/// <para><b>`25-181`: the owner's own live, hand-granted extra counts toward this limit immediately.</b>
/// Without this, <c>GetOwnerSeatSummaryHandler</c>'s own displayed limit would be a lie - a number the
/// owner console shows as available capacity that this, the actual gate every invite/promote/restore
/// call site goes through, would still refuse at the pre-grant ceiling. Resolved fresh every call
/// (<paramref name="cancellationToken"/> aside, no caching - CLAUDE.md rule 8: a capacity decision reads
/// live), against this method's own <see cref="IClock.UtcNow"/> - the identical "the caller resolves it
/// against its own clock" shape <c>EntitlementWatchdogJob</c>'s own remarks already state for the
/// reconciliation side of the same feature.</para>
///
/// <para><b>Must be called inside the caller's own ambient transaction</b> - the identical contract
/// <see cref="IOperatorRoleRepository.LockAndGetHeldSeatHolderIdsAsync"/> itself states, since the whole
/// point is that the row lock this method takes stays held until the caller's own write (an invite
/// redemption, a role change, a seat toggle) either commits or rolls back with it.</para>
/// </summary>
public sealed class OperatorRoleSeatCapacity(
    IOperatorRoleRepository operatorRoles,
    ISiteRepository sites,
    IOwnerSeatGrantStore ownerSeatGrants,
    IPendingOperatorInviteSeatReadStore pendingInvites,
    IClock clock)
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

        // `25-181`: the platform owner's own hand-granted extra, added on top of the billing-derived
        // baseline - the identical shape `EntitlementWatchdogJob`/`GetOwnerSeatSummaryHandler` already
        // apply for the reconciliation and display sides of this same feature, reused here rather than
        // reinvented for the third, load-bearing side: the actual capacity gate.
        var extra = await ownerSeatGrants.GetEffectiveExtraAsync(
            siteId, RoleSeatLimits.OwnerGrantRoleFor(roleName), clock.UtcNow, cancellationToken);
        var limit = RoleSeatLimits.LimitFor(roleName, site) + extra;
        return new RoleSeatCapacityCheck(holderIds.Count >= limit, limit);
    }

    /// <summary>
    /// `26-241`: the create-time sibling of <see cref="CheckAsync"/> - is there a free seat for
    /// <paramref name="roleName"/> right now, counting current holders <em>plus</em> already-outstanding
    /// unredeemed invites for that same role (<paramref name="roleId"/>)? <c>CreateOperatorInviteHandler</c>
    /// runs this once per requested role before sending, so a sent-but-unredeemed invite reserves its
    /// seat and an admin cannot over-invite past the limit and have every invitee redeem past it.
    ///
    /// <para><b>Why an unlocked read here, where <see cref="CheckAsync"/> takes a row lock.</b>
    /// <see cref="CheckAsync"/> is the authoritative compare-and-set the actual seat write commits inside
    /// (`CLAUDE.md` rule 8) - two concurrent redemptions must serialise on the `sites` row so exactly one
    /// wins the last seat. This check is a <em>preventive gate</em> at send time: it reads live from the
    /// database (never a cache), but a benign race between two concurrent <em>sends</em> is acceptable
    /// because the row-locked redeem-time <see cref="CheckAsync"/> remains the hard cap - the worst a lost
    /// send-race does is let one invite through that redemption then refuses, which is exactly the
    /// pre-`26-241` behaviour for over-invited seats, now the rare exception rather than the rule. Adding
    /// a lock here would mean opening a transaction on the create path purely to serialise a preventive
    /// check, buying correctness the redeem-time gate already owns.</para>
    ///
    /// <para><b>Excludes the invite being created</b> - it is not yet persisted when this runs, so
    /// <see cref="IPendingOperatorInviteSeatReadStore.CountOutstandingForRoleAsync"/> counts only invites
    /// that already exist; the new one reserves its slot against the <em>next</em> send, not against
    /// itself.</para>
    /// </summary>
    public async Task<RoleSeatCapacityCheck> CheckForNewInviteAsync(
        SiteId siteId, string roleName, Guid roleId, CancellationToken cancellationToken)
    {
        // Unlocked holder read (GetHeldSeatHolderIdsAsync, not LockAndGet...) - see this method's own
        // remarks for why the create path deliberately does not take the row lock CheckAsync takes.
        var holderIds = await operatorRoles.GetHeldSeatHolderIdsAsync(siteId, roleName, cancellationToken);

        var site = await sites.GetByIdAsync(siteId, cancellationToken);
        if (site is null)
        {
            throw new InvalidOperationException(
                $"Site {siteId.Value} was not found while checking role-seat capacity for role '{roleName}' - "
                + "a foreign key should have prevented this.");
        }

        var extra = await ownerSeatGrants.GetEffectiveExtraAsync(
            siteId, RoleSeatLimits.OwnerGrantRoleFor(roleName), clock.UtcNow, cancellationToken);
        var pending = await pendingInvites.CountOutstandingForRoleAsync(siteId, roleId, clock.UtcNow, cancellationToken);
        var limit = RoleSeatLimits.LimitFor(roleName, site) + extra;
        return new RoleSeatCapacityCheck(holderIds.Count + pending >= limit, limit);
    }
}

/// <summary>One capacity check's own outcome - <see cref="Limit"/> travels with
/// <see cref="IsAtCapacity"/> so every caller can build its own existing refusal shape
/// (<c>ConversationErrors.OperatorSeatLimitReached</c>/<c>OperatorAdminLimitReached</c>,
/// <c>OperatorInviteRedemptionResult.SeatLimitReached</c>/<c>AdminLimitReached</c>) without a second
/// read.</summary>
public sealed record RoleSeatCapacityCheck(bool IsAtCapacity, int Limit);
