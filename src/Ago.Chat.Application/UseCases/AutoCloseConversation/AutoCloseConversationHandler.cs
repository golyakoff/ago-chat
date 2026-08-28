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
/// <para><b>The one guard <see cref="Conversation.Close"/> itself does not enforce, made explicit
/// here.</b> <see cref="Conversation.Close"/> only refuses a conversation that is already `Closed` - it
/// has no opinion on `Waiting`, because <c>CloseConversationHandler</c> never needed one: comparing
/// <c>command.OperatorId</c> against <see cref="Conversation.OperatorId"/> (`null` on a `Waiting`
/// conversation) already returns `Forbidden` for that case as a side effect of the ownership check.
/// This handler has no `OperatorId` to compare against, so without an explicit state check here, a
/// conversation that regressed from `Assigned` to `Waiting` between
/// `AutoCloseInactiveConversationsQuery`'s candidate scan and this handler actually running (`4-04`'s
/// disconnect-grace release landing in that exact window, for instance) would be closed anyway - the
/// backlog item's own scope note is explicit that `Waiting` conversations are not this item's call to
/// make. <see cref="HandleAndSaveAsync"/> checks it directly, first.</para>
///
/// <para><b>Why <see cref="HandleAndSaveAsync"/> does not also catch
/// <see cref="InvalidConversationStateException"/> around the <see cref="Conversation.Close"/> call
/// the way <c>CloseConversationHandler.CloseAndSaveAsync</c> does.</b> Found while writing this
/// handler's own fails-before table: the guard above is a strict superset of what
/// <see cref="Conversation.Close"/> itself refuses (`State != Assigned` covers both `Waiting` and
/// `Closed`; `Close` only ever throws for `Closed`), so by the time execution reaches the `Close` call
/// below, `State == Assigned` is already established and the exception is provably unreachable through
/// this call path - unlike `CloseConversationHandler`, whose weaker OperatorId-equality guard does
/// <em>not</em> catch "the same operator retries their own already-closed conversation" (`OperatorId`
/// survives `Close()`, so the comparison still passes), which is exactly why that handler's own
/// try/catch is load-bearing rather than redundant. Keeping an unreachable catch here would read as
/// protection this handler does not actually have, and the next person to touch this file has no way
/// to tell "defensive" from "dead" without re-deriving this same argument - so it is removed rather
/// than left in for symmetry with its sibling.</para>
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
        // See this class's own remarks: the one check CloseConversationHandler gets for free from its
        // OperatorId comparison, and this handler must make explicit because it has no OperatorId to
        // compare. A strict superset of what Conversation.Close() itself refuses (both Waiting and
        // Closed fail `!= Assigned`), which is also why HandleAndSaveAsync never needs to catch
        // InvalidConversationStateException around the Close() call below - see this class's own
        // remarks on why that catch was removed rather than kept for symmetry with
        // CloseConversationHandler.
        if (conversation.State != ConversationState.Assigned)
        {
            return ConversationErrors.InvalidState(
                $"Conversation {conversation.Id.Value} is {conversation.State}, not Assigned; auto-close only touches Assigned conversations.");
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
            // never had an OperatorId to begin with, and the state guard above already established
            // conversation.State == Assigned, where OperatorId is always populated
            // (Conversation.AssignTo's own invariant).
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
