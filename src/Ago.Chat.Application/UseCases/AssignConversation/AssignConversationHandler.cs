using Ago.Chat.Application.Abstractions;
using Ago.Chat.Domain;
using Ago.Platform.Kernel;

namespace Ago.Chat.Application.UseCases.AssignConversation;

public sealed class AssignConversationHandler(
    IConversationRepository conversations,
    IPermissionChecker permissions,
    IClock clock)
{
    public async Task<Result> HandleAsync(AssignConversation command, CancellationToken cancellationToken)
    {
        var allowed = await permissions.HasPermissionAsync(
            command.OperatorId, command.SiteId, Permission.ConversationAssign, cancellationToken);
        if (!allowed)
        {
            return ConversationErrors.Forbidden("Operator does not have permission to claim conversations for this site.");
        }

        var conversation = await conversations.GetByIdAsync(command.ConversationId, cancellationToken);
        if (conversation is null)
        {
            return ConversationErrors.NotFound(command.ConversationId.Value);
        }

        try
        {
            return await AssignAndSaveAsync(conversation, command, cancellationToken);
        }
        catch (ConversationConcurrencyConflictException)
        {
            // `6-08`: same reasoning as CloseConversationHandler's own retry - a concurrent writer
            // (typically a message send bumping this row's `xmin`) committed between the read above and
            // the save inside AssignAndSaveAsync. Reloading and reapplying AssignTo is safe because it
            // re-validates its own invariant against fresh data, including its `3-03` same-operator
            // no-op: OperatorHub.JoinConversationAsync calls this on every join, including a reconnect,
            // so the common case here is literally "the same operator, asking again" - never a case
            // where retrying could hand the conversation to the wrong caller. A genuine claim race
            // between two *different* operators still surfaces as Conversation.InvalidState on the
            // fresh read, exactly as it would with no retry at all. One retry only, not a loop: a second
            // ConversationConcurrencyConflictException becomes Conversation.ConcurrencyConflict (409).
            var fresh = await conversations.GetByIdAsync(command.ConversationId, cancellationToken);
            if (fresh is null)
            {
                return ConversationErrors.NotFound(command.ConversationId.Value);
            }

            try
            {
                return await AssignAndSaveAsync(fresh, command, cancellationToken);
            }
            catch (ConversationConcurrencyConflictException)
            {
                return ConversationErrors.ConcurrencyConflict(command.ConversationId.Value);
            }
        }
    }

    private async Task<Result> AssignAndSaveAsync(
        Conversation conversation, AssignConversation command, CancellationToken cancellationToken)
    {
        try
        {
            conversation.AssignTo(command.OperatorId, clock.UtcNow);
        }
        catch (InvalidConversationStateException ex)
        {
            return ConversationErrors.InvalidState(ex.Message);
        }

        // May throw ConversationConcurrencyConflictException (IConversationRepository's own contract,
        // `6-08`) - left to propagate to HandleAsync's retry wrapper, same reasoning as
        // CloseConversationHandler.CloseAndSaveAsync.
        await conversations.SaveAsync(conversation, cancellationToken);
        return Result.Success();
    }
}
