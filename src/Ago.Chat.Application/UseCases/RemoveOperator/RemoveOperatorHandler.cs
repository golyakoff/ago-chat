using Ago.Chat.Application.Abstractions;
using Ago.Chat.Application.Mapping;
using Ago.Chat.Domain;
using Ago.Platform.Abstractions;
using Ago.Platform.Kernel;

namespace Ago.Chat.Application.UseCases.RemoveOperator;

/// <summary>
/// `13-03`: an ordinary single-aggregate write plus one outbox row (<see cref="Operator.Remove"/> raises
/// <see cref="OperatorRemoved"/>) - the same "plain, unbatched per-request handler, `IOutboxWriter`
/// injected directly" shape <c>UpdateWidgetConfigHandler</c>'s own remarks describe, not one of this
/// item's wider multi-aggregate appliers: this write only ever touches the one <see cref="Operator"/>
/// row plus the outbox. Releasing the removed operator's `Assigned` conversations back to `Waiting` is
/// deliberately not done here - `Ago.Chat.Worker`'s own <c>OperatorRemovedConsumer</c> is, out of this
/// request's transaction, reusing the existing <c>OperatorConversationReleaser</c>.
///
/// <para><b>`23-26`: the last-manager guard.</b> Not "you cannot remove yourself" - self-removal is
/// legitimate, and a rule against it would refuse that legitimate case while still permitting the
/// actually broken one (one manager removing the other, or two managers removing each other in either
/// order). The real invariant is one line: at least one non-removed operator on the site holds
/// <see cref="Permission.SiteManageOperators"/>, and it says nothing about who the caller is. Only a
/// target who themselves holds the permission can possibly break it by being removed, so the count is
/// skipped entirely for the ordinary case (removing an operator who never held it) - and when it does
/// run, <see cref="IUnitOfWork"/> and <see cref="IPermissionChecker.CountNonRemovedHoldersAsync"/>
/// together make it a compare-and-set read taken inside this write's own transaction (CLAUDE.md rule
/// 8), never a cached count - see that port's own remarks for why two concurrent removals of a site's
/// last two managers is exactly the race this closes.</para>
/// </summary>
public sealed class RemoveOperatorHandler(
    IOperatorRepository operators, IPermissionChecker permissions, IUnitOfWork unitOfWork,
    IOutboxWriter outbox, IIdGenerator idGenerator, IClock clock)
{
    public async Task<Result> HandleAsync(RemoveOperator command, CancellationToken cancellationToken)
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

        // `23-26`: a terminal fact about *this* operator's own role assignment - granting/revoking a
        // role is out of this item's own scope, so nothing in this request can change it concurrently,
        // and it is safe to read before the transaction below. The same "checked before any lock is
        // taken" split OperatorInviteRedemptionRepository's own remarks draw for its own terminal,
        // unlocked checks.
        var targetManagesOperators = await permissions.HasPermissionAsync(
            target.Id, command.SiteId, Permission.SiteManageOperators, cancellationToken);

        await using var transaction = await unitOfWork.BeginTransactionAsync(cancellationToken);

        if (targetManagesOperators)
        {
            var remainingHolders = await permissions.CountNonRemovedHoldersAsync(
                command.SiteId, Permission.SiteManageOperators, cancellationToken);
            if (remainingHolders <= 1)
            {
                // Only this transaction's own lock read counted this operator - the count includes the
                // target itself (not yet removed), so "1" means nobody else remains. Disposed without a
                // commit below - rolls back, the same "return inside the `await using` block" shape
                // TransferConversationHandler's own refusals already use.
                return ConversationErrors.OperatorIsLastManager();
            }
        }

        var removedAt = clock.UtcNow;
        target.Remove(removedAt);
        var removed = target.DomainEvents.OfType<OperatorRemoved>().Single();
        outbox.Enqueue(OperatorRemovedMapper.ToEnvelope(removed, idGenerator));

        // `22-05`/`adr/0093`: revocation is the same fact this event always carries, becoming empty -
        // not a different event. Skipped only when the removed operator never linked an external
        // identity (an unredeemed invite that was later removed), the same "nothing to project"
        // guard `SiteRegistrationRepository`'s own publisher uses, for the identical reason: no
        // subject means no projection row anywhere could exist to revoke.
        if (target.ExternalSubjectId is { } removedSubject)
        {
            outbox.Enqueue(RoleAssignmentsChangedMapper.ToEnvelope(
                removedSubject, command.SiteId.Value, [], removedAt, idGenerator));
        }

        target.ClearDomainEvents();

        await operators.SaveAsync(target, cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return Result.Success();
    }
}
