using Ago.Chat.Application.Abstractions;
using Ago.Chat.Application.Mapping;
using Ago.Chat.Domain;
using Ago.Platform.Abstractions;
using Ago.Platform.Kernel;
using Microsoft.Extensions.Logging;

namespace Ago.Chat.Application.UseCases.CloseConversation;

/// <summary>
/// `6-02`: the first real caller of `Conversation.Close()` since Stage 1 - the domain method has
/// existed with no use case, no endpoint, and no integration event wired to it. Operator-only (a
/// visitor ending a chat session client-side is not the same as closing the record - the backlog
/// item's own scope note); only the operator already assigned to *this* conversation may close it,
/// checked here rather than inside <see cref="Conversation.Close"/> itself - unlike
/// <c>AddOperatorMessage</c>, <c>Close</c> takes no <see cref="OperatorId"/> parameter to check
/// against, so the "is this caller the one assigned to this conversation" fact
/// (adr/0016: a fact about the conversation, not a permission) is this handler's own job, the same
/// "RBAC answers may this operator act at all, a per-conversation comparison answers on this one"
/// split <see cref="Application.UseCases.ConfirmAttachment.ConfirmAttachmentHandler.HandleAsOperatorAsync"/>
/// already draws for `conversation:send`. A conversation that was never assigned (still `Waiting`) is
/// therefore not closable by anyone yet either - closing is scoped to "the operator handling this
/// conversation ends it," not a moderator force-close of an unclaimed one; that would be new scope.
///
/// Injects <see cref="IOutboxWriter"/> directly rather than staging through
/// <c>Infrastructure.Postgres.Pipeline</c> - the same "plain, unbatched per-request handler, no shared
/// multi-conversation transaction to coordinate" shape <see cref="Application.UseCases.ConfirmAttachment.ConfirmAttachmentHandler"/>
/// uses (adr/0005: state change and integration event, one transaction, one `SaveChangesAsync`).
///
/// <para>`6-09`: also the place an operator's capacity claim is handed back, because closing is the
/// ordinary, ubiquitous way an assignment ends and until this item existed nothing released it -
/// <c>active_chats</c> only ever came down when the operator's last connection anywhere dropped
/// (`4-04`'s <c>OperatorConversationReleaser</c>), so an operator working through conversations one
/// at a time ratcheted down to zero usable capacity and the site's waiting queue silently stopped
/// being served (found live by `7-04`'s <c>assignment-contention</c> run, not by inspection).
/// <b>Here rather than in a <c>ConversationEnded</c> consumer in <c>Ago.Chat.Worker</c></b>, which was
/// the real alternative: the outbox would have made the release survive a crash between commit and
/// decrement, at the cost of a whole new consumer, queue binding and redelivery-idempotency argument
/// for a single <c>UPDATE</c> - and of freeing the slot only after the dispatcher and broker hop, when
/// the entire user-visible point is that an operator who just finished a chat can be given the next
/// one now. The residual is stated plainly rather than designed away: a process death in the window
/// between the commit below and <c>ReleaseAsync</c> leaks exactly one slot, which is the pre-`6-09`
/// behaviour for that one conversation, bounded, and still recovered by the disconnect sweep when that
/// operator eventually goes offline.</para>
///
/// <para>`6-10`: that release is the one statement in this handler that can lose a Postgres deadlock,
/// because the assignment engine writes the same <c>operators</c> row from a transaction that holds
/// several of them (`adr/0037`, and the captured graph in `6-10`'s backlog item). The adapter retries
/// it; if it still cannot land, the close stays successful and the leak above is the outcome - see
/// <see cref="Application.Abstractions.OperatorCapacityContentionException"/> and the catch below.
/// What must never happen is an operator seeing `40P01` for pressing "close".</para>
/// </summary>
public sealed class CloseConversationHandler(
    IConversationRepository conversations,
    IConversationAssignmentLog assignmentLog,
    IPermissionChecker permissions,
    IOperatorCapacity capacity,
    IOutboxWriter outbox,
    IIdGenerator idGenerator,
    IClock clock,
    ILogger<CloseConversationHandler> logger)
{
    public async Task<Result> HandleAsync(CloseConversation command, CancellationToken cancellationToken)
    {
        var allowed = await permissions.HasPermissionAsync(
            command.OperatorId, command.SiteId, Permission.ConversationClose, cancellationToken);
        if (!allowed)
        {
            return ConversationErrors.Forbidden("Operator does not have permission to close conversations for this site.");
        }

        var conversation = await conversations.GetByIdAsync(command.ConversationId, cancellationToken);
        if (conversation is null)
        {
            return ConversationErrors.NotFound(command.ConversationId.Value);
        }

        try
        {
            return await CloseAndSaveAsync(conversation, command, cancellationToken);
        }
        catch (ConversationConcurrencyConflictException)
        {
            // `6-08`: a concurrent writer (typically a message send bumping this row's `xmin`, `6-06`'s
            // load-proof finding) committed between the read above and the save inside
            // CloseAndSaveAsync - not that closing itself is wrong. Reloading and reapplying is safe
            // exactly because Close() re-validates its own invariant against whatever is actually on
            // disk now: if a second racing close (or any other state change) makes the fresh row
            // unclosable, that surfaces as the ordinary Conversation.InvalidState/Forbidden result
            // below, not a swallowed exception - the retry never bypasses a real business conflict, it
            // only re-asks the same question against fresh data. Retried once, not in a loop: a second
            // ConversationConcurrencyConflictException in the same request means a third writer landed
            // inside this already-narrow window, and at that point the honest answer is "retry the
            // whole request" (Conversation.ConcurrencyConflict, 409), matching this item's "single
            // transparent retry, or a clean 409" scope - never an unbounded retry loop.
            var fresh = await conversations.GetByIdAsync(command.ConversationId, cancellationToken);
            if (fresh is null)
            {
                return ConversationErrors.NotFound(command.ConversationId.Value);
            }

            try
            {
                return await CloseAndSaveAsync(fresh, command, cancellationToken);
            }
            catch (ConversationConcurrencyConflictException)
            {
                return ConversationErrors.ConcurrencyConflict(command.ConversationId.Value);
            }
        }
    }

    private async Task<Result> CloseAndSaveAsync(
        Conversation conversation, CloseConversation command, CancellationToken cancellationToken)
    {
        if (conversation.OperatorId != command.OperatorId)
        {
            return ConversationErrors.Forbidden("This operator is not assigned to this conversation.");
        }

        var now = clock.UtcNow;
        bool consumedCapacityClaim;
        try
        {
            consumedCapacityClaim = conversation.Close(now);
        }
        catch (InvalidConversationStateException ex)
        {
            return ConversationErrors.InvalidState(ex.Message);
        }

        var domainEvent = conversation.DomainEvents.OfType<ConversationClosed>().Single();
        outbox.Enqueue(ConversationClosedMapper.ToEnvelope(domainEvent, idGenerator));
        conversation.ClearDomainEvents();

        // `23-03`: closes without opening, one of the six writers `conversation_assignments` needs a
        // real one for. A conversation assigned before this item shipped has no open interval to find
        // (backfill is out of scope) - IConversationAssignmentLog.CloseOpenAsync's own contract makes
        // that a silent no-op, not a failure.
        await assignmentLog.CloseOpenAsync(conversation.Id, now, cancellationToken);

        // May throw ConversationConcurrencyConflictException (IConversationRepository's own contract,
        // `6-08`) - left to propagate to HandleAsync's retry wrapper rather than caught here, so this
        // method stays "the one attempt" and HandleAsync stays the one place that owns the
        // retry-once policy. The interval close staged above rides this same SaveChangesAsync - see
        // IConversationAssignmentLog's own remarks.
        await conversations.SaveAsync(conversation, cancellationToken);

        // `6-09`: strictly after the save, never before. A release ahead of the save would be undone
        // by nothing when SaveAsync loses on `xmin` - the conversation would still be Assigned with
        // its slot already given back, and the operator over-subscribable by one for the rest of that
        // slot's life. After the save, the only failure left is a leak in the crash window, which is
        // the strictly safer direction and the one this handler documents rather than hides. The
        // retry path above cannot double-release: it reloads the row this save just closed, and
        // Conversation.Close() throws on an already-closed row before any release is reached.
        if (consumedCapacityClaim)
        {
            try
            {
                // command.OperatorId, not conversation.OperatorId - the guard at the top of this method
                // has already established they are the same, and this avoids a null-forgiving operator on
                // a property whose non-nullness is only implied by the state machine.
                await capacity.ReleaseAsync(command.OperatorId, cancellationToken);
            }
            catch (OperatorCapacityContentionException ex)
            {
                // `6-10`: the close is already committed. Turning this into a failed request would be
                // a lie about what happened and would make things worse, not better - the operator
                // would retry, the retry would be rejected as already-closed (`Conversation.InvalidState`),
                // and the slot still would not come back. So the request stays successful and the
                // residual is exactly the one the paragraph above already names for a process death in
                // this same window: one leaked slot, inert, recovered when that operator next goes
                // offline (`4-04`'s sweep). Logged at Warning rather than swallowed silently, and
                // counted (`ago.chat.assignment.capacity_release_deadlocks{outcome="abandoned"}`),
                // because a leak nobody can see is how `6-09`'s original bug went unnoticed for a
                // stage and a half. `adr/0037` argues the bound this arrives after.
                logger.LogWarning(
                    ex,
                    "Conversation {ConversationId} closed, but operator {OperatorId}'s capacity slot could not be released after {Attempts} attempt(s); one slot leaks until that operator next disconnects.",
                    command.ConversationId.Value,
                    command.OperatorId.Value,
                    ex.Attempts);
            }
        }

        return Result.Success();
    }
}
