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
/// <para><b>Promoting somebody to administrator is never refused for capacity.</b> An administrator is
/// a role, not a purchase (`adr/0151` keeps entitlement and permission apart; `RegisterSiteHandler`
/// seeds roles per site with no reference to billing at all) - this handler was drafted once with a
/// tier-priced administrator ceiling modelled on an unread `ago-business` pricing document and that
/// draft is exactly the coupling `adr/0151` forbids, so it was removed rather than shipped. See this
/// item's own report for the finding. If a real per-tier administrator limit is ever wanted, it is a
/// new, explicitly-scoped item that can read the actual pricing decision, not something this handler
/// infers.</para>
///
/// <para><b>The guard only runs for the one case it protects.</b> A change that revokes
/// `site:manage_operators` from its current holder takes the site-row lock and counts; every other
/// change (granting the permission, or moving between two roles that neither hold it) takes no lock at
/// all - the same "skip the count entirely when the target never held the permission" optimisation
/// <c>RemoveOperatorHandler</c>'s own remarks already state for its own analogous case.</para>
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
    IUnitOfWork unitOfWork,
    IRoleChangeRecordRepository roleChangeRecords,
    IOutboxWriter outbox,
    IIdGenerator idGenerator,
    IClock clock)
{
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
