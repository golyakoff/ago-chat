using Ago.Chat.Application.Abstractions;
using Ago.Chat.Application.Mapping;
using Ago.Chat.Domain;
using Ago.Platform.Abstractions;
using Ago.Platform.Kernel;
using Microsoft.Extensions.Logging;

namespace Ago.Chat.Application.UseCases.ReleaseInactiveConversation;

/// <summary>
/// `25-118`: the widget-only "release" pass's own handler - a single-conversation version of
/// `Ago.Chat.Worker.OperatorConversationReleaser.ReleaseAllAsync` (`4-04`), wearing
/// <see cref="AutoCloseConversation.AutoCloseConversationHandler"/>'s own shape (a `Result`-returning
/// handler resolved per-candidate from a fresh scope by `AutoCloseInactiveConversationsJob`, with the
/// identical defensive re-check that candidate scan and actual handling are not the same instant).
///
/// <para><b>Why this exists as a second handler rather than widening `AutoCloseConversationHandler`
/// itself.</b> Closing and releasing are genuinely different domain operations
/// (<see cref="Conversation.Close"/> vs. <see cref="Conversation.ReleaseToQueue"/>), ending in different
/// states, with different outbox contracts (<see cref="Contracts.ConversationEnded"/> vs.
/// <see cref="Contracts.ConversationReleasedToQueue"/>) - the same "two small handlers sharing no
/// business logic beyond the capacity-release tail" reasoning
/// <see cref="AutoCloseConversation.AutoCloseConversationHandler"/>'s own remarks already give for why
/// it is a second handler next to <see cref="CloseConversation.CloseConversationHandler"/>, restated
/// once more for a third sibling instead of a widened second.</para>
///
/// <para><b>The state guard is `!= Assigned`, not "not yet past `WidgetCloseWindow`".</b> By the time
/// this handler runs, the only two ways the scanned candidate could have stopped qualifying are: a
/// message arrived (a real visitor or operator write - re-checked implicitly, because the *query* only
/// ever selects the id, and a state guard here catches every other kind of change a message would also
/// have caused, such as none - see the note below on why a message alone does not change `State`), or
/// the conversation already left `Assigned` for some other reason (an operator's own close, `4-04`'s own
/// disconnect-release beating this job to it, or - once this item ships - this very release pass having
/// already run for it on an earlier, still-in-flight scope). None of those needs a fresh timestamp
/// comparison: `Conversation.ReleaseToQueue` only has one precondition (`State == Assigned`), so
/// re-establishing that is re-establishing everything the domain method itself needs re-verified. A
/// message that arrived *inside* the window is caught upstream, by
/// <see cref="AutoCloseInactiveConversationsQuery.FindStaleAssignedBatchAsync"/>'s own
/// no-recent-message `NOT EXISTS`, not down here - the same division of labour
/// <see cref="AutoCloseConversation.AutoCloseConversationHandler"/> already accepts for its own
/// candidate.</para>
/// </summary>
public sealed class ReleaseInactiveConversationHandler(
    IConversationRepository conversations,
    IConversationAssignmentLog assignmentLog,
    IOperatorCapacity capacity,
    IOutboxWriter outbox,
    IIdGenerator idGenerator,
    IClock clock,
    ILogger<ReleaseInactiveConversationHandler> logger)
{
    public async Task<Result> HandleAsync(ReleaseInactiveConversation command, CancellationToken cancellationToken)
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
            // `6-08`'s own retry-once shape, reused verbatim from AutoCloseConversationHandler: a
            // concurrent writer committed between the read above and the save inside
            // HandleAndSaveAsync. Reloading and reapplying is safe because both the state guard and
            // ReleaseToQueue itself re-validate against whatever is actually on disk now.
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
        // See this class's own remarks: Conversation.ReleaseToQueue itself only refuses a non-Assigned
        // conversation, but making the guard explicit here (rather than catching
        // InvalidConversationStateException around the call below) matches
        // AutoCloseConversationHandler's own shape and gives a caller-legible Result rather than an
        // exception for what is, on this job's own cadence, an entirely ordinary outcome.
        if (conversation.State != ConversationState.Assigned)
        {
            return ConversationErrors.InvalidState(
                $"Conversation {conversation.Id.Value} is {conversation.State}, not Assigned; release only touches Assigned conversations.");
        }

        var now = clock.UtcNow;
        var siteId = conversation.SiteId;
        var visitorId = conversation.VisitorId;
        // Captured before ReleaseToQueue, unlike AutoCloseConversationHandler's own read of
        // conversation.OperatorId *after* calling Close() - Close() leaves OperatorId set (it is not
        // part of that state transition's own invariant), but ReleaseToQueue's whole point is to null
        // it out as part of returning the conversation to the queue, so this is the last instant this
        // aggregate still knows who to release capacity for.
        var operatorId = conversation.OperatorId!.Value;
        var consumedCapacityClaim = conversation.ReleaseToQueue(now);

        var domainEvent = conversation.DomainEvents.OfType<ConversationReleased>().Single();
        outbox.Enqueue(ConversationReleasedToQueueMapper.ToEnvelope(domainEvent, siteId, visitorId, idGenerator));
        conversation.ClearDomainEvents();

        // `23-03`: closes without opening, the identical shape `OperatorConversationReleaser.ReleaseAllAsync`
        // already uses for the operator-disconnect case this handler generalises to a per-conversation
        // caller - staged on the same unit of work `conversations.SaveAsync` flushes below.
        await assignmentLog.CloseOpenAsync(conversation.Id, now, cancellationToken);

        // May throw ConversationConcurrencyConflictException (IConversationRepository's own contract,
        // `6-08`) - left to propagate to HandleAsync's retry wrapper, exactly as
        // AutoCloseConversationHandler's own HandleAndSaveAsync does.
        await conversations.SaveAsync(conversation, cancellationToken);

        // `6-09`: strictly after the save - the identical ordering AutoCloseConversationHandler and
        // CloseConversationHandler both use, for the identical reason (a release ahead of a save that
        // then loses to `xmin` would give back capacity for an assignment that is still, on disk,
        // intact).
        if (consumedCapacityClaim)
        {
            try
            {
                await capacity.ReleaseAsync(operatorId, cancellationToken);
            }
            catch (OperatorCapacityContentionException ex)
            {
                // `6-10`'s same residual, reused: the release is already committed, so this stays a
                // successful result and the leak is logged rather than turned into a failure that would
                // misreport what happened.
                logger.LogWarning(
                    ex,
                    "Conversation {ConversationId} auto-released to queue for inactivity, but operator {OperatorId}'s capacity slot could not be released after {Attempts} attempt(s); one slot leaks until that operator next disconnects.",
                    conversation.Id.Value,
                    operatorId.Value,
                    ex.Attempts);
            }
        }

        return Result.Success();
    }
}
