using Ago.Chat.Application.Abstractions;
using Ago.Chat.Application.Mapping;
using Ago.Chat.Domain;
using Ago.Platform.Abstractions;
using Ago.Platform.Kernel;
using Microsoft.Extensions.Logging;

namespace Ago.Chat.Application.UseCases.AutoCloseConversation;

/// <summary>
/// `18-06`: the system-initiated twin of
/// <see cref="Application.UseCases.CloseConversation.CloseConversationHandler"/> - same domain path
/// (<see cref="Conversation.Close"/>, the outbox `ConversationClosed` -&gt; `ConversationEnded` mapping,
/// `6-09`'s release-strictly-after-save capacity path), reached by
/// `Ago.Chat.Worker.AutoCloseInactiveConversationsJob` instead of an operator's own request.
///
/// <para><b>Why a second handler rather than a nullable <c>OperatorId</c> on the first.</b>
/// <c>CloseConversationHandler.HandleAsync</c> checks <see cref="IPermissionChecker"/> and then "is the
/// caller the operator already assigned to this conversation" - both meaningless for a scheduled sweep
/// with no operator behind it at all. Threading an "OperatorId: null means system, skip both checks"
/// branch through that handler would make its authorisation conditional on who is calling, which is
/// exactly the shape a reviewer (or a future caller) can get backwards - pass null by a copy-paste
/// mistake and skip a check that should have run. Two small handlers sharing one domain call
/// (<see cref="Conversation.Close"/>) and one capacity-release path keeps each one's authorisation
/// unconditional: this one always applies to a system close, the other always applies to an operator's
/// own.</para>
///
/// <para><b>`18-06`'s original guard here rejected anything but `Assigned` - `25-118` narrowed it to
/// reject only `Closed`.</b> Until `25-118`, a `Waiting` conversation was never this item's call to
/// make at all (the backlog item's own scope note said so explicitly, and
/// `AutoCloseInactiveConversationsQuery`'s scan structurally never produced one as a candidate anyway),
/// so the guard below used to read `State != Assigned` - a strict superset of what
/// <see cref="Conversation.Close"/> itself refuses, added defensively against a conversation that
/// regressed from `Assigned` to `Waiting` between the scan and this handler actually running (`4-04`'s
/// disconnect-grace release landing in that exact window, for instance). `25-118`'s own "Answered"
/// design deliberately reverses that scope decision for the widget bucket: a `Waiting` widget
/// conversation past `WidgetCloseWindow` is now a real, intended candidate for this exact handler
/// (`AutoCloseInactiveConversationsQuery.FindStaleWidgetBatchIncludingWaitingAsync`, wired by
/// `AutoCloseInactiveConversationsJob`'s new close pass) - so the old guard would now silently refuse
/// the very rows this item exists to reach. The guard below is narrowed to match exactly what
/// <see cref="Conversation.Close"/> itself refuses (`State == Closed`, nothing more), which is honest
/// about what invariant this handler is actually enforcing: "never close an already-closed
/// conversation," not "never touch anything but Assigned." The channel-kind bucket is unaffected - its
/// own query (`AutoCloseInactiveConversationsQuery.FindStaleAssignedBatchAsync`, given a real
/// `ChannelKind`) still only ever selects `Assigned` rows, so this handler never actually sees a
/// `Waiting` channel-kind candidate to widen the guard for in practice.</para>
///
/// <para><b>Why <see cref="HandleAndSaveAsync"/> does not also catch
/// <see cref="InvalidConversationStateException"/> around the <see cref="Conversation.Close"/> call
/// the way <c>CloseConversationHandler.CloseAndSaveAsync</c> does.</b> The guard below is now exactly
/// what <see cref="Conversation.Close"/> itself refuses, not merely a superset of it, so by the time
/// execution reaches the `Close` call, `State != Closed` is already established and the exception is
/// provably unreachable through this call path - unlike `CloseConversationHandler`, whose weaker
/// OperatorId-equality guard does <em>not</em> catch "the same operator retries their own
/// already-closed conversation" (`OperatorId` survives `Close()`, so the comparison still passes),
/// which is exactly why that handler's own try/catch is load-bearing rather than redundant. Keeping an
/// unreachable catch here would read as protection this handler does not actually have, and the next
/// person to touch this file has no way to tell "defensive" from "dead" without re-deriving this same
/// argument - so it is removed rather than left in for symmetry with its sibling.</para>
/// </summary>
public sealed class AutoCloseConversationHandler(
    IConversationRepository conversations,
    IOperatorCapacity capacity,
    IOutboxWriter outbox,
    IIdGenerator idGenerator,
    IClock clock,
    ILogger<AutoCloseConversationHandler> logger)
{
    public async Task<Result> HandleAsync(AutoCloseConversation command, CancellationToken cancellationToken)
    {
        var conversation = await conversations.GetByIdAsync(command.ConversationId, cancellationToken);
        if (conversation is null)
        {
            return ConversationErrors.NotFound(command.ConversationId.Value);
        }

        try
        {
            return await HandleAndSaveAsync(conversation, cancellationToken);
        }
        catch (ConversationConcurrencyConflictException)
        {
            // `6-08`'s own retry-once shape, reused verbatim: a concurrent writer (a new message, an
            // operator's own close, a disconnect release) committed between the read above and the
            // save inside HandleAndSaveAsync. Reloading and reapplying is safe because both the state
            // guard and Close() itself re-validate against whatever is actually on disk now - a second
            // race inside this already-narrow window is treated the same way CloseConversationHandler
            // treats it: give up and let the next job cycle re-evaluate, never an unbounded retry loop.
            var fresh = await conversations.GetByIdAsync(command.ConversationId, cancellationToken);
            if (fresh is null)
            {
                return ConversationErrors.NotFound(command.ConversationId.Value);
            }

            try
            {
                return await HandleAndSaveAsync(fresh, cancellationToken);
            }
            catch (ConversationConcurrencyConflictException)
            {
                return ConversationErrors.ConcurrencyConflict(command.ConversationId.Value);
            }
        }
    }

    private async Task<Result> HandleAndSaveAsync(Conversation conversation, CancellationToken cancellationToken)
    {
        // See this class's own remarks: `25-118` narrowed this from `!= Assigned` to `== Closed` -
        // exactly what Conversation.Close() itself refuses, no more - so a Waiting widget conversation
        // past WidgetCloseWindow (the new query variant's whole point) is no longer turned away here.
        // This is also why HandleAndSaveAsync never needs to catch InvalidConversationStateException
        // around the Close() call below - see this class's own remarks on why that catch was removed
        // rather than kept for symmetry with CloseConversationHandler.
        if (conversation.State == ConversationState.Closed)
        {
            return ConversationErrors.InvalidState(
                $"Conversation {conversation.Id.Value} is already {ConversationState.Closed}.");
        }

        var consumedCapacityClaim = conversation.Close(clock.UtcNow);

        var domainEvent = conversation.DomainEvents.OfType<ConversationClosed>().Single();
        outbox.Enqueue(ConversationClosedMapper.ToEnvelope(domainEvent, idGenerator));
        conversation.ClearDomainEvents();

        // May throw ConversationConcurrencyConflictException (IConversationRepository's own contract,
        // `6-08`) - left to propagate to HandleAsync's retry wrapper, exactly as
        // CloseConversationHandler's own CloseAndSaveAsync does.
        await conversations.SaveAsync(conversation, cancellationToken);

        if (consumedCapacityClaim)
        {
            // Read directly from the aggregate rather than a command field, unlike
            // CloseConversationHandler (which has one to avoid a null-forgiving read) - this handler
            // never had an OperatorId to begin with. `25-118`: the guard above no longer establishes
            // conversation.State == Assigned (a Waiting conversation is now an accepted candidate too),
            // but consumedCapacityClaim can only be true here because HoldsCapacityClaim can only be
            // true while State == Assigned (Conversation.AssignTo's own invariant) - ReleaseToQueue and
            // the "never assigned" case both leave it false, and Close() itself does not flip it back on
            // - so reaching this branch still implies conversation.OperatorId was populated by the same
            // AssignTo call that set the claim, and Close() never clears OperatorId.
            var operatorId = conversation.OperatorId!.Value;
            try
            {
                await capacity.ReleaseAsync(operatorId, cancellationToken);
            }
            catch (OperatorCapacityContentionException ex)
            {
                // `6-10`'s same residual, reused: the close is already committed, so this stays a
                // successful result and the leak is logged rather than turned into a failure that would
                // misreport what happened.
                logger.LogWarning(
                    ex,
                    "Conversation {ConversationId} auto-closed for inactivity, but operator {OperatorId}'s capacity slot could not be released after {Attempts} attempt(s); one slot leaks until that operator next disconnects.",
                    conversation.Id.Value,
                    operatorId.Value,
                    ex.Attempts);
            }
        }

        return Result.Success();
    }
}
