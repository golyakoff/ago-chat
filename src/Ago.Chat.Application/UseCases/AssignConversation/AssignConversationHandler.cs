using Ago.Chat.Application.Abstractions;
using Ago.Chat.Domain;
using Ago.Platform.Kernel;

namespace Ago.Chat.Application.UseCases.AssignConversation;

/// <summary>
/// `17-01`: this handler is the one place in the codebase where an operator's *own* site claim and a
/// conversation they merely name by id first meet, and until this item it never compared the two -
/// see <see cref="HandleAsync"/>'s belongs-to-site guard and the reasoning there. Everything an
/// operator can subsequently do to a conversation (read its history, send into it, close it, reach
/// its attachments, see the visitor's presence) is gated on being its *assigned* operator, so this
/// is the choke point those checks all rest on: if a caller can become the assignee of another
/// tenant's conversation, every one of those participant checks answers "yes" for them afterwards.
/// </summary>
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
        if (conversation is null || conversation.SiteId != command.SiteId)
        {
            // `17-01`: the second half of this condition closed a real cross-tenant hole, not a
            // theoretical one. `command.SiteId` comes from the caller's own token claim, so the
            // permission check above only ever proves "this operator may claim conversations *for
            // their own site*" - it says nothing about the site the conversation named by
            // `ConversationId` actually belongs to, and nothing else in the chain re-derives it.
            // Without this comparison, an operator of site B could claim any *Waiting* conversation
            // of site A by id, and would then pass the `conversation.OperatorId == RequestedBy`
            // participant check that every read/write path downstream relies on
            // (`GetConversationHistoryHandler`, `SendOperatorMessageHandler`,
            // `CloseConversationHandler`, `GetVisitorPresenceHandler`, the attachment handlers).
            //
            // NotFound, not Forbidden - the same info-hiding shape `DeleteAttachmentHandler` and
            // `RevokeWebhookEndpointHandler` already use for the identical situation: a row belonging
            // to a different tenant must read exactly like one that does not exist, never
            // "it exists, just not yours".
            //
            // Here rather than inside `Conversation.AssignTo`: the aggregate's other two callers
            // (`SkipLockedAssignmentClaimer`/`RedisLockAssignmentClaimer`) resolve their operator
            // *from* the conversation's own site, so passing a site down to the domain method would
            // have them compare `conversation.SiteId` against itself - a guard that looks like one
            // and can never fire. This handler is the only place where two independently-sourced
            // facts (the caller's claimed site, the conversation's real site) actually meet.
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
