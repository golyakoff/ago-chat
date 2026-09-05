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
/// <para>`23-04`: every real (non-reconnect) call through this handler is now a deliberate take -
/// `decisions.md` §2's whole point. Before this item the hub join that reaches this handler
/// (`OperatorHub.JoinConversationAsync`) had no reachable UI, so `23-03` left it writing
/// <see cref="ConversationAssignmentSource.Assigned"/> for lack of anything to tell it apart from the
/// engine's own automatic assignments. This item gives it both a reachable path (the rail/`/search`,
/// and the new <c>POST /api/v1/conversations/{id}/claim</c> route this same handler now also serves)
/// and its own value, <see cref="ConversationAssignmentSource.Taken"/>, and it always charges capacity
/// for the transition - see <see cref="AssignAndSaveAsync"/>'s own remarks on why that write is
/// unconditional and why it sits inside an explicit transaction alongside the interval and the
/// conversation's own save.</para>
/// </summary>
public sealed class AssignConversationHandler(
    IConversationRepository conversations,
    IConversationAssignmentLog assignmentLog,
    IPermissionChecker permissions,
    IOperatorCapacity capacity,
    IUnitOfWork unitOfWork,
    IIdGenerator idGenerator,
    IClock clock)
{
    /// <summary>
    /// `23-04`: matches `TransferConversationHandler.TransactionAttempts` - the same proven bound, not
    /// a fresh guess. This handler now touches an `operators` row inside its own explicit transaction
    /// for the first time, which makes it a new participant in the exact accepted, data-dependent
    /// deadlock cycle `adr/0037`/`concurrency.md` document for the assignment engine's own batches -
    /// the identical shape `TransferConversationHandler` already absorbs by retrying the whole
    /// transaction rather than a single statement. That handler's own remarks record that 2 attempts
    /// with no backoff let *zero* transfers through a real sustained storm and 5-with-jitter did not;
    /// this item reuses the number and the formula rather than re-deriving them, and has not itself run
    /// a load test to re-prove the bound for this specific caller (CLAUDE.md rule 7: this is a reused,
    /// previously-measured bound, not a fresh performance claim) - see the commit-prep report for what
    /// that leaves unverified.
    /// </summary>
    private const int TransactionAttempts = 5;

    public async Task<Result> HandleAsync(AssignConversation command, CancellationToken cancellationToken)
    {
        var allowed = await permissions.HasPermissionAsync(
            command.OperatorId, command.SiteId, Permission.ConversationAssign, cancellationToken);
        if (!allowed)
        {
            return ConversationErrors.Forbidden("Operator does not have permission to claim conversations for this site.");
        }

        for (var attempt = 1; ; attempt++)
        {
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
            catch (ConversationConcurrencyConflictException) when (attempt < TransactionAttempts)
            {
                // `6-08`'s reasoning carried into a bounded loop rather than a single retry - a
                // concurrent writer (typically a message send, or another operator's own take of the
                // same conversation) committed between the read above and the save inside
                // AssignAndSaveAsync. Reloading and reapplying is safe because AssignTo re-validates
                // its own invariant against whatever is on disk now, including its `3-03` same-operator
                // no-op - the common case here is still "the same operator, asking again" on a
                // reconnect. Jittered for the identical thundering-herd reason
                // `TransferConversationHandler`'s own remarks give.
                await Task.Delay(TimeSpan.FromMilliseconds(Random.Shared.Next(4, 16) * attempt), cancellationToken);
            }
            catch (OperatorCapacityContentionException) when (attempt < TransactionAttempts)
            {
                // Nothing committed - a `40P01` aborted the whole transaction, whether it came from
                // this handler's own claim or an assignment batch queued behind this operator's row.
                // Retrying re-runs the whole attempt from a fresh read, the same reasoning
                // TransferConversationHandler's own catch gives for its own capacity contention.
                await Task.Delay(TimeSpan.FromMilliseconds(Random.Shared.Next(4, 16) * attempt), cancellationToken);
            }
            catch (ConversationConcurrencyConflictException)
            {
                // A second conflict inside this already-generous bound means a genuinely busy row, not
                // this handler's own bug. Conversation.InvalidState is returned separately, inside
                // AssignAndSaveAsync's own re-validation - this is only reached when the row itself
                // would not sit still long enough to save, exactly `6-08`'s original distinction.
                return ConversationErrors.ConcurrencyConflict(command.ConversationId.Value);
            }
            catch (OperatorCapacityContentionException)
            {
                return ConversationErrors.ClaimContended(command.ConversationId.Value);
            }
        }
    }

    /// <summary>
    /// `23-04`: <see cref="Conversation.AssignTo"/> runs first, before any database write - its own
    /// same-operator reconnect no-op and its `Waiting`-only invariant are both checked entirely
    /// in-memory, so a reconnect or a lost race never opens a transaction at all, and a real transition
    /// only ever reaches the transaction below once <see cref="ConversationAssigned"/> is known to have
    /// been raised.
    ///
    /// <para><b>Why the capacity claim is unconditional, inside the same explicit transaction as the
    /// interval and the conversation's own save.</b> `decisions.md` §2: capacity gates the automatic
    /// assigner only, never a person's own choice, so <see cref="IOperatorCapacity.ClaimAsync"/> cannot
    /// fail on the compare the way <see cref="IOperatorCapacity.TryClaimAsync"/> can - there is nothing
    /// to check. What still has to be atomic is "this operator holds the conversation" and "this
    /// operator's counter reflects it": a crash between an unconditional claim and the conversation's
    /// own commit would either strand a slot on an operator holding nothing for it, or leave
    /// <see cref="Conversation.HoldsCapacityClaim"/> `true` with no real increment behind it - the exact
    /// under-count `HoldsCapacityClaim`'s own remarks warn is worse than a leak, because it lets the
    /// engine over-subscribe that operator. `IUnitOfWork` is what makes that atomic here, the identical
    /// port and reasoning `TransferConversationHandler` already established for its own two capacity
    /// statements.</para>
    ///
    /// <para><b>Two operators racing to take the same conversation</b> resolves the same way two
    /// transfers of the same conversation already do: both read the row `Waiting`, both open a
    /// transaction, both claim capacity for themselves (different `operators` rows - no conflict
    /// between them there), both stage an interval and call <c>AssignTo</c> in memory - and one of them
    /// then loses, on either of two things a real Postgres actually enforces, not one. The conversation
    /// row's own `xmin` is the more obvious guard; found live, by this exact test under this exact
    /// race, is that `23-03`'s own partial unique index (<c>ix_conversation_assignments_open</c>, "at
    /// most one open interval per conversation") can be the one that actually fires first - EF stages
    /// the new interval as an Added entity and the conversation as Modified, and Added entities execute
    /// before Modified ones within one <c>SaveChangesAsync</c>, so the loser can hit the unique index
    /// before its own `UPDATE` ever reaches the `xmin` check. Left untranslated, that surfaced as a raw
    /// <c>DbUpdateException</c> wrapping Postgres's `23505`, not
    /// <see cref="ConversationConcurrencyConflictException"/> -
    /// <c>ConversationRepository.SaveAsync</c> now translates both shapes identically, because both
    /// mean the identical thing: someone else already committed a conflicting fact about this
    /// conversation. Either way the loser's whole transaction never commits - its capacity claim rolls
    /// back with it, so <c>active_chats</c> only ever rises by exactly one - and
    /// <see cref="HandleAsync"/>'s retry reloads a conversation already `Assigned` to the winner, where
    /// a fresh <see cref="Conversation.AssignTo"/> throws <see cref="InvalidConversationStateException"/>
    /// and this method returns <see cref="ConversationErrors.InvalidState"/>.</para>
    /// </summary>
    private async Task<Result> AssignAndSaveAsync(
        Conversation conversation, AssignConversation command, CancellationToken cancellationToken)
    {
        var now = clock.UtcNow;
        try
        {
            conversation.AssignTo(command.OperatorId, now, holdsCapacityClaim: true);
        }
        catch (InvalidConversationStateException ex)
        {
            return ConversationErrors.InvalidState(ex.Message);
        }

        if (!conversation.DomainEvents.OfType<ConversationAssigned>().Any())
        {
            // `3-03`'s same-operator reconnect no-op: AssignTo returned before raising the event, so
            // there is nothing to charge, nothing to log, and nothing to save - no transaction is ever
            // opened for this path. Cleared unconditionally anyway, the same "never leave a tracked
            // instance holding stale events" reasoning `23-03` already established for this branch, even
            // though this particular call added none itself.
            conversation.ClearDomainEvents();
            return Result.Success();
        }

        await using var transaction = await unitOfWork.BeginTransactionAsync(cancellationToken);

        // `23-04`: unconditional - see IOperatorCapacity.ClaimAsync's own remarks. Every reachable
        // caller of this handler now represents a deliberate take, so this always charges capacity;
        // `decisions.md` §2 is explicit that the counter may end up past `capacity` afterwards and that
        // this is the intended state, not a bug for a future change to "fix".
        await capacity.ClaimAsync(command.OperatorId, cancellationToken);

        // `23-03`/`23-04`: source Taken, not Assigned - see ConversationAssignmentSource.Taken's own
        // remarks for why this path's writes changed value the moment it gained a reachable UI.
        assignmentLog.Open(ConversationAssignmentInterval.Open(
            new ConversationAssignmentId(idGenerator.NewId(now)), command.SiteId, conversation.Id,
            command.OperatorId, ConversationAssignmentSource.Taken, now));

        conversation.ClearDomainEvents();

        // May throw ConversationConcurrencyConflictException (IConversationRepository's own contract,
        // `6-08`) - left to propagate to HandleAsync's retry loop. Runs inside this method's ambient
        // transaction exactly like the capacity claim and the interval open just staged: a conflict
        // here aborts everything this attempt did, rolling the claim back with it.
        await conversations.SaveAsync(conversation, cancellationToken);

        await transaction.CommitAsync(cancellationToken);
        return Result.Success();
    }
}
