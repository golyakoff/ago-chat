using Ago.Chat.Application.Abstractions;
using Ago.Chat.Application.Mapping;
using Ago.Chat.Domain;
using Ago.Platform.Abstractions;
using Ago.Platform.Kernel;

namespace Ago.Chat.Application.UseCases.ChangeOperatorRole;

/// <summary>
/// `23-72`: "an administrator can change an existing colleague's role, both directions" - the one write
/// this codebase has ever needed against an *existing* operator's role assignment (bootstrap
/// registration and invite redemption both only ever assign a role once, at creation).
///
/// <para><b>The last-administrator guard is the same primitive `23-26` already built for removal, not a
/// second copy of it.</b> <see cref="RemoveOperator.RemoveOperatorHandler"/>'s own invariant - "at least
/// one non-removed operator on the site holds <see cref="Permission.SiteManageOperators"/>" - applies
/// unchanged to "the last role that grants it" (this item's own backlog: "removing the last role that
/// grants it" is exactly what demoting a site's only administrator to `"Operator"` does).
/// <see cref="IPermissionChecker.CountNonRemovedHoldersAsync"/> is reused verbatim, inside the identical
/// <see cref="IUnitOfWork"/>-scoped transaction and site-row lock <c>RemoveOperatorHandler</c> already
/// takes - two independent write paths sharing one invariant and one lock, never two invariants that
/// could drift apart.</para>
///
/// <para><b>`25-25`: promoting somebody to administrator is now refused for capacity, and the history of
/// why it once was not is worth keeping.</b> The paragraph this replaces read: "an administrator is a
/// role, not a purchase... this handler was drafted once with a tier-priced administrator ceiling
/// modelled on an <em>unread</em> `ago-business` pricing document, and that draft... was removed rather
/// than shipped... if a real per-tier administrator limit is ever wanted, it is a new, explicitly-scoped
/// item that can read the actual pricing decision, not something this handler infers." That item is this
/// one. `ago-business` decisions `0011`/`0012` are now read, not inferred - <see cref="Site.AdminLimit"/>
/// is set from them (via <see cref="SubscriptionTierBands.ResolveAdminLimit"/>) at the one place a tier
/// is ever decided (`Site.ActivateSubscription`), and this handler only ever reads that already-resolved
/// number back. `adr/0151`'s own line still holds and is not being crossed a second time: this handler
/// still infers nothing about pricing itself, and still never talks to a permission the way the rejected
/// draft did - it counts by role name (<see cref="IOperatorRoleRepository.CountNonRemovedHoldersAsync(SiteId,string,System.Threading.CancellationToken)"/>),
/// the same distinction that repository's own remarks draw against <see cref="IPermissionChecker.CountNonRemovedHoldersAsync"/>.
/// Refused with <see cref="ConversationErrors.OperatorAdminLimitReached"/>, the identical `402` shape
/// <see cref="ConversationErrors.OperatorSeatLimitReached"/> already gives the analogous seat-capacity
/// refusal on a different write path.</para>
///
/// <para><b>Each guard only runs for the one case it protects.</b> A change that revokes
/// `site:manage_operators` from its current holder takes the site-row lock and counts against
/// <see cref="Permission.SiteManageOperators"/>'s own holders; a change that grants the seeded
/// `"Admin"` role to someone who did not already hold it (`25-25`) takes the identical lock and counts
/// against <see cref="Site.AdminLimit"/> instead; every other change (moving between two roles that
/// neither grants nor revokes the role, or a no-op re-selection of the role already held) takes no lock
/// at all - the same "skip the count entirely when the target's own state cannot move" optimisation
/// <c>RemoveOperatorHandler</c>'s own remarks already state for its own analogous case, extended to the
/// second guard on the identical terms.</para>
///
/// <para><b>Deliberately replaces the whole role assignment, not adds to it.</b> A colleague named by
/// this handler is set to hold <em>exactly</em> <see cref="ChangeOperatorRole.NewRoleName"/> afterward,
/// mirroring the single-role-per-invite shape <see cref="OperatorInviteRedemptionRepository"/> already
/// establishes for a freshly redeemed operator - not the two-role shape
/// <see cref="ISiteRegistrationRepository"/> gives the account's own founder at registration (both
/// seeded roles at once, so they can administer and take conversations from day one). Holding both roles
/// stays a registration-only artifact; this item does not build a way for an ordinary colleague to hold
/// two roles at once, because nothing in its own Scope asked for that and the console has no UI today
/// that could offer "add a role" as distinct from "change to this role" without inventing one.</para>
///
/// <para><b><see cref="Operator.HoldsSeat"/> is untouched.</b> `23-71` made "may sign in and administer"
/// and "may be routed a conversation" two different facts about a person - a role change answers only
/// the first, so this handler never reads or writes <see cref="Operator.HoldsSeat"/> at all. A colleague
/// promoted to Admin keeps whatever seat they already held (or did not); nothing here revokes or grants
/// one, the identical separation <see cref="OperatorInviteRedemptionRepository"/>'s own remarks describe
/// for a freshly redeemed administrator.</para>
/// </summary>
public sealed class ChangeOperatorRoleHandler(
    IOperatorRepository operators,
    IRoleRepository roles,
    IOperatorRoleRepository operatorRoles,
    IPermissionChecker permissions,
    ISiteRepository sites,
    IUnitOfWork unitOfWork,
    IRoleChangeRecordRepository roleChangeRecords,
    IOutboxWriter outbox,
    IIdGenerator idGenerator,
    IClock clock)
{
    /// <summary>`25-25`: the same bare literal <c>OperatorInviteRedemptionRepository</c>'s own
    /// <c>AdminRoleName</c> uses, for the identical reason - no named-role catalogue exists yet for
    /// this codebase to reach for instead.</summary>
    private const string AdminRoleName = "Admin";

    public async Task<Result> HandleAsync(ChangeOperatorRole command, CancellationToken cancellationToken)
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

        if (target.RemovedAt is not null)
        {
            return ConversationErrors.OperatorAlreadyRemoved(command.TargetOperatorId.Value);
        }

        var newRole = await roles.GetByNameAsync(command.SiteId, command.NewRoleName, cancellationToken);
        if (newRole is null)
        {
            return ConversationErrors.OperatorRoleNotFound(
                $"Site {command.SiteId.Value} has no role named '{command.NewRoleName}'.");
        }

        // Terminal facts about the target's *current* assignment, read before any lock is taken - the
        // same "checked before any lock is taken" split RemoveOperatorHandler's own remarks draw for the
        // identical question. Neither is the actual capacity decision - each branch below re-reads the
        // live count from inside its own transaction and lock, so a stale read here can only cause a
        // redundant (never a skipped) lock/count when the true state has moved since this read.
        var targetManagesOperators = await permissions.HasPermissionAsync(
            target.Id, command.SiteId, Permission.SiteManageOperators, cancellationToken);
        var newRoleManagesOperators = newRole.Permissions.Contains(Permission.SiteManageOperators.Value);
        var previousRoleNames = await operatorRoles.GetRoleNamesAsync(target.Id, cancellationToken);
        // `25-25`: the promotion guard's own terminal, pre-lock fact - the identical "checked before
        // any lock is taken" split the paragraph above already draws for targetManagesOperators/
        // newRoleManagesOperators. A stale read here can only cause a redundant (never a skipped)
        // lock/count below, since the promotion branch re-reads the live count from inside its own
        // transaction and lock.
        var wasAdmin = previousRoleNames.Contains(AdminRoleName);

        await using var transaction = await unitOfWork.BeginTransactionAsync(cancellationToken);

        if (targetManagesOperators && !newRoleManagesOperators)
        {
            var remainingHolders = await permissions.CountNonRemovedHoldersAsync(
                command.SiteId, Permission.SiteManageOperators, cancellationToken);
            if (remainingHolders <= 1)
            {
                // The count includes the target itself (its own role has not changed yet) - "1" means
                // nobody else remains. Disposed without a commit below - rolls back.
                return ConversationErrors.OperatorIsLastManager();
            }
        }

        // `25-25`: the Administrator-seat guard, mirroring the shape right above on the opposite
        // direction - a change that grants the seeded "Admin" role to someone who did not already hold
        // it (a no-op re-selection of a role already held never reaches here, `wasAdmin` above is
        // already true for it). Skipped entirely, no lock taken, for every other change - promoting to
        // any other role, or a change that touches neither role name.
        if (!wasAdmin && command.NewRoleName == AdminRoleName)
        {
            var adminCount = await operatorRoles.CountNonRemovedHoldersAsync(command.SiteId, AdminRoleName, cancellationToken);
            var site = await sites.GetByIdAsync(command.SiteId, cancellationToken);
            if (site is null)
            {
                // A foreign key (OperatorConfiguration.HasOne<Site>) should make this unreachable - the
                // same "should have prevented this" throw this codebase's other site-row locks already
                // raise for the identical impossible case (OperatorInviteRedemptionRepository.
                // LockSiteAndReadCapacityAsync's own remarks).
                throw new InvalidOperationException(
                    $"Site {command.SiteId.Value} was not found while changing an operator's role - " +
                    "a foreign key should have prevented this.");
            }

            if (adminCount >= site.AdminLimit)
            {
                // The count does not yet include the target (its own role has not changed yet, and it
                // was not already an Administrator) - "at or above the limit" already means no room
                // for one more. Disposed without a commit below - rolls back.
                return ConversationErrors.OperatorAdminLimitReached(site.AdminLimit);
            }
        }

        var now = clock.UtcNow;
        await operatorRoles.ReplaceRoleAsync(target.Id, newRole.Id, cancellationToken);
        await roleChangeRecords.RecordAsync(
            new RoleChangeRecordToWrite(
                idGenerator.NewId(now), command.SiteId, command.RequestedBy, target.Id,
                previousRoleNames, command.NewRoleName, now),
            cancellationToken);

        // `22-05`/`adr/0093`: the changed operator's newly resolved permission set - the same fact
        // invite redemption and removal both already publish on every write that can change what an
        // external subject may do, so the cross-product role-assignment projection never lags behind
        // this write.
        if (target.ExternalSubjectId is { } subject)
        {
            outbox.Enqueue(RoleAssignmentsChangedMapper.ToEnvelope(
                subject, command.SiteId.Value, newRole.Permissions, now, idGenerator));
        }

        await transaction.CommitAsync(cancellationToken);

        return Result.Success();
    }
}
