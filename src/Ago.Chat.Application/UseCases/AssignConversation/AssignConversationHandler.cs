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
///
/// <para>`23-03`: also one of the six writers of <c>conversation_assignments</c> - opens an interval
/// with <see cref="ConversationAssignmentSource.Assigned"/> whenever <see cref="Domain.Conversation.AssignTo"/>
/// actually transitions the conversation, and stays silent for the same-operator reconnect no-op that
/// method also handles (<c>OperatorHub.JoinConversationAsync</c> calls this on every join, including a
/// reconnect - see <see cref="AssignAndSaveAsync"/>'s own remarks on how the two are told apart). Why
/// <c>Assigned</c> and not a distinct value for a human's own deliberate claim: see
/// <see cref="ConversationAssignmentSource.Assigned"/>'s own remarks - that distinction does not exist
/// in the code yet, so this item does not invent one in the data either.</para>
/// </summary>
public sealed class AssignConversationHandler(
    IConversationRepository conversations,
    IConversationAssignmentLog assignmentLog,
    IPermissionChecker permissions,
    IIdGenerator idGenerator,
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
        var now = clock.UtcNow;
        try
        {
            conversation.AssignTo(command.OperatorId, now);
        }
        catch (InvalidConversationStateException ex)
        {
            return ConversationErrors.InvalidState(ex.Message);
        }

        // `23-03`: ConversationAssigned is only raised when AssignTo actually transitioned the
        // conversation - its own same-operator reconnect no-op returns before adding it. Checking the
        // event rather than, say, "was the conversation Waiting before this call" keeps this in step
        // with the aggregate's own definition of "did anything happen" without this handler having to
        // duplicate it (the identical event-as-signal idiom the two Ago.Chat.Worker claimers and
        // TransferConversationHandler already use to decide what belongs in their own outbox rows).
        if (conversation.DomainEvents.OfType<ConversationAssigned>().Any())
        {
            assignmentLog.Open(ConversationAssignmentInterval.Open(
                new ConversationAssignmentId(idGenerator.NewId(now)), command.SiteId, conversation.Id,
                command.OperatorId, ConversationAssignmentSource.Assigned, now));
        }

        // `23-03`: cleared unconditionally, even on the no-op path where nothing was added - every
        // other reader of DomainEvents in this codebase (the two claimers, TransferConversationHandler,
        // CloseConversationHandler) clears immediately after reading, and this handler had never needed
        // to before now because it enqueued no outbox row. Skipping it here is a real bug, not a
        // cosmetic one: SaveAsync does not clear on success (only on a concurrency-conflict retry,
        // ConversationRepository's own remarks), so a *second* call against the identical in-memory
        // aggregate - the reconnect no-op this whole check exists to recognise - would still see the
        // *first* call's stale ConversationAssigned sitting in the list and open a second interval for
        // it. Found by AssignConversationHandlerTests.HandleAsync_WhenTheSameOperatorReconnects_OpensNoSecondInterval
        // failing against a repository that (correctly) hands back the same tracked instance twice.
        conversation.ClearDomainEvents();

        // May throw ConversationConcurrencyConflictException (IConversationRepository's own contract,
        // `6-08`) - left to propagate to HandleAsync's retry wrapper, same reasoning as
        // CloseConversationHandler.CloseAndSaveAsync. The interval Open above is staged, not committed,
        // on the same AgoChatDbContext this SaveAsync flushes - see IConversationAssignmentLog's own
        // remarks for why that is what keeps the two in one transaction (CLAUDE.md rule 4).
        await conversations.SaveAsync(conversation, cancellationToken);
        return Result.Success();
    }
}
