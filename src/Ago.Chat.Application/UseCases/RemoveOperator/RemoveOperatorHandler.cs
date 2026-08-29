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

        target.Remove(clock.UtcNow);
        var removed = target.DomainEvents.OfType<OperatorRemoved>().Single();
        outbox.Enqueue(OperatorRemovedMapper.ToEnvelope(removed, idGenerator));
        target.ClearDomainEvents();

        await operators.SaveAsync(target, cancellationToken);

        return Result.Success();
    }
}
