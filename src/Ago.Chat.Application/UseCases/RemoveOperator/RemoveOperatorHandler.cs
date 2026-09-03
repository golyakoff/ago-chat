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
/// </summary>
public sealed class RemoveOperatorHandler(
    IOperatorRepository operators, IPermissionChecker permissions, IOutboxWriter outbox, IIdGenerator idGenerator, IClock clock)
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

        return Result.Success();
    }
}
