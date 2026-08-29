using Ago.Chat.Application.Abstractions;
using Ago.Chat.Application.Mapping;
using Ago.Chat.Domain;
using Ago.Platform.Abstractions;
using Ago.Platform.Kernel;

namespace Ago.Chat.Application.UseCases.TransferConversation;

/// <summary>
/// `18-02`: `4-02`'s assignment machinery used a second way, per the backlog item's own framing - a
/// conversation moves from the operator who holds it to a named colleague, capacity released on one
/// side and claimed on the other, in one Postgres transaction or none of them (the item's own Scope).
///
/// <para><b>Why this needs <see cref="IUnitOfWork"/> when no other handler in this codebase does.</b>
/// Every other multi-statement write here gets atomicity for free from one aggregate's own
/// <c>SaveChangesAsync</c> - <c>CloseConversationHandler</c>'s own remarks are explicit that its
/// capacity release is deliberately <em>not</em> part of that transaction, because releasing after an
/// already-committed close only ever risks a bounded, self-healing leak. A transfer cannot accept that
/// residual on either side: releasing the source before a save that then loses on `xmin` would
/// over-subscribe them for nothing (exactly `adr/0033`'s original objection to an early release), and
/// claiming the target without also committing the conversation's own new `OperatorId` would strand a
/// slot on an operator holding no conversation for it. The two capacity statements, the conversation's
/// state change, and its outbox row have to rise and fall together - see <see cref="IUnitOfWork"/>'s
/// own remarks for why that port exists rather than this handler reaching for a
/// <c>DbContext</c> directly.</para>
///
/// <para><b>The retry unit is the whole transaction, not a statement.</b> `6-10`/`adr/0037` gave
/// <c>OperatorCapacityStore.ReleaseAsync</c> its own bounded, jittered, five-attempt retry precisely
/// because it owns no transaction of its own - a deadlock there aborts nothing but itself, so
/// re-issuing the one statement is free and correct. Everything this handler does happens inside one
/// caller-owned transaction, so a `40P01` anywhere in it - on the claim, on the release, or a
/// concurrent writer bumping the conversation's own `xmin` mid-save - aborts the entire attempt with
/// nothing committed. There is no statement to retry in place; the only thing that can be retried is
/// the attempt itself, from a fresh read.</para>
///
/// <para><b>The bound: 5 attempts, jittered, not `6-08`'s bare single retry - revised after measuring,
/// not assumed.</b> The first version of this handler bounded <see cref="TransactionAttempts"/> at 2
/// (one retry, no backoff), reasoning that `ReleaseAsync`'s five-attempt bound is calibrated against
/// the cost of an *abandoned* retry - a leaked slot - and this handler's transaction is all-or-nothing,
/// so there is no leak to weigh against extra attempts, only latency. That reasoning about the
/// *residual* was correct and still holds. The *conclusion* it led to - "so fewer attempts are fine" -
/// was wrong, and <c>TransferConversationConcurrencyTests.TransferringRacesTheAssignmentEngine_...</c>
/// is what found it wrong: under a sustained storm (the same shape
/// <c>ClosesStormingAssignmentBatches_...</c> uses, 200+ real server-side deadlocks over 15s), a bare
/// single retry with no backoff let <em>zero</em> transfers succeed, run after run. The mechanism is
/// exactly what <c>OperatorCapacityStore.ReleaseAsync</c>'s own comment already warns about for a
/// different statement: retrying immediately, with no jitter, re-issues the failed attempt back into
/// the same contended row at the same instant every other loser does, recreating the next cycle
/// instead of escaping it. Two attempts in lockstep is not materially different from one.
///
/// <para>Once the actual failure mode is "no backoff, not too few attempts", the residual argument
/// above cuts the other way from where it first pointed: because an abandoned retry costs nothing but
/// the caller's own patience - no leak, no partial state, nothing for a disconnect sweep to recover
/// later - there is *more* room to retry generously here than `ReleaseAsync` has, not less, since
/// `ReleaseAsync`'s five was itself bounded by "how long may an operator's close wait for a slot the
/// disconnect sweep already recovers anyway" (`adr/0037`), a question this handler's failure mode does
/// not even ask. <see cref="TransactionAttempts"/> is 5 - matching `ReleaseAsync`'s own number because
/// it is a proven bound in this exact codebase against this exact class of contention, not because the
/// original per-attempt reasoning transferred unchanged - with the identical jittered backoff formula
/// (`Random 4-16ms x attempt`) between attempts, for the identical thundering-herd reason.</para>
///
/// <para>This is not a proof that 5 is enough under arbitrary load, and CLAUDE.md rule 7 forbids
/// claiming a number nobody measured: what is measured is that 5-with-jitter gets transfers through
/// the same storm that made 2-with-no-jitter get zero through, repeatedly. See the test's own remarks
/// and the honest residual this leaves, stated in the commit-prep report rather than rounded up.</para>
///
/// <para><b>Lock order.</b> This transaction is a new participant in the accepted, un-fixable
/// engine-vs-engine cycle `adr/0037` already documents (a batch holding several `operators` rows in
/// data-dependent order) - that risk is not addressed here, cannot be addressed here, and the retry
/// above is exactly the treatment `adr/0037` already prescribes for it. What *is* addressed here,
/// because it is this handler's own to cause and its own to fix, is a transfer inverting against
/// <em>another transfer</em>: two transfers of the same conversation, or a swap between the same two
/// operators in opposite directions, would otherwise take the same two `operators` rows in opposite
/// program order and deadlock against each other for no reason the engine has anything to do with. See
/// <see cref="TransferAndSaveAsync"/>'s own remarks for the canonical order that rules this out.</para>
/// </summary>
public sealed class TransferConversationHandler(
    IConversationRepository conversations,
    IOperatorRepository operators,
    IPermissionChecker permissions,
    IOperatorCapacity capacity,
    IUnitOfWork unitOfWork,
    IOutboxWriter outbox,
    IIdGenerator idGenerator,
    IClock clock)
{
    /// <summary>See the type's own remarks: measured, not assumed - 5, matching
    /// <c>OperatorCapacityStore.ReleaseAsync</c>'s own bound, after a bare single retry (this item's
    /// first version) let zero transfers through a real storm, repeatedly.</summary>
    private const int TransactionAttempts = 5;

    public async Task<Result> HandleAsync(TransferConversation command, CancellationToken cancellationToken)
    {
        if (command.FromOperatorId == command.ToOperatorId)
        {
            // Cheapest possible rejection, before any permission or repository work - see
            // ConversationErrors.TransferTargetIsCurrentOperator's own remarks.
            return ConversationErrors.TransferTargetIsCurrentOperator();
        }

        var allowed = await permissions.HasPermissionAsync(
            command.FromOperatorId, command.SiteId, Permission.ConversationAssign, cancellationToken);
        if (!allowed)
        {
            return ConversationErrors.Forbidden("Operator does not have permission to transfer conversations for this site.");
        }

        // `18-02`'s own HoldsSeat/RemovedAt decision: refused here, visibly, rather than left to
        // capacity or sign-in to make moot. There is no transfer-target-picker endpoint in this
        // codebase yet to have filtered a seat-less or removed operator out of a caller's choices
        // upstream, and a seat-less/removed operator who cannot sign in would never learn a
        // conversation had been handed to them - a silent dead end for the visitor, not merely an
        // administrative inconsistency. IOperatorRepository.GetByIdAsync(OperatorId,SiteId,...)
        // deliberately does not filter on either flag (it answers "does this id belong to this
        // site", nothing about eligibility), so this handler is where that fact and this use case's
        // own requirement actually meet - the same "the aggregate applies, the caller enforces the
        // cross-aggregate rule" split this codebase draws everywhere else (AssignConversationHandler's
        // own site-guard remarks). Scoped by SiteId in the same call, which is also what makes a
        // cross-site transfer target structurally impossible rather than merely refused, per the
        // backlog item's own Out of scope note - there is no site parameter to get wrong here, unlike
        // a hypothetical id-only lookup a caller would have to remember to double-check.
        var target = await operators.GetByIdAsync(command.ToOperatorId, command.SiteId, cancellationToken);
        if (target is null || !target.HoldsSeat || target.RemovedAt is not null)
        {
            return ConversationErrors.TransferTargetNotEligible(command.ToOperatorId.Value);
        }

        for (var attempt = 1; ; attempt++)
        {
            var conversation = await conversations.GetByIdAsync(command.ConversationId, cancellationToken);
            if (conversation is null || conversation.SiteId != command.SiteId)
            {
                // Same info-hiding shape AssignConversationHandler's own cross-tenant guard uses: a
                // conversation belonging to a different site must read exactly like one that does not
                // exist.
                return ConversationErrors.NotFound(command.ConversationId.Value);
            }

            try
            {
                return await TransferAndSaveAsync(conversation, command, cancellationToken);
            }
            catch (ConversationConcurrencyConflictException) when (attempt < TransactionAttempts)
            {
                // Nothing committed - the whole transaction aborted on a concurrent writer's xmin bump
                // (typically a message send, `6-06`'s own finding, landing mid-transfer). Reload and
                // retry, jittered - see the type's own remarks on why a bare retry with no backoff
                // measurably failed to get transfers through a real storm.
                await Task.Delay(TimeSpan.FromMilliseconds(Random.Shared.Next(4, 16) * attempt), cancellationToken);
            }
            catch (OperatorCapacityContentionException) when (attempt < TransactionAttempts)
            {
                // Nothing committed here either - a `40P01` aborted the whole transaction, whether it
                // came from the claim, the release, or (per OperatorCapacityStore's own remarks) an
                // assignment batch queued behind either of this transaction's two `operators` rows.
                // Unlike CloseConversationHandler's own contention outcome, there is no already-
                // committed close to protect and no leaked slot to accept - retrying re-runs the whole
                // attempt from a fresh read, because the first attempt changed nothing. Jittered for
                // the identical reason OperatorCapacityStore.ReleaseAsync's own retry is: a detected
                // cycle usually has several losers queued on the same row, and retrying them all in
                // lockstep is how the next cycle gets built.
                await Task.Delay(TimeSpan.FromMilliseconds(Random.Shared.Next(4, 16) * attempt), cancellationToken);
            }
            catch (ConversationConcurrencyConflictException)
            {
                return ConversationErrors.ConcurrencyConflict(command.ConversationId.Value);
            }
            catch (OperatorCapacityContentionException)
            {
                return ConversationErrors.TransferContended(command.ConversationId.Value);
            }
        }
    }

    private async Task<Result> TransferAndSaveAsync(
        Conversation conversation, TransferConversation command, CancellationToken cancellationToken)
    {
        if (conversation.OperatorId != command.FromOperatorId)
        {
            // Covers both a genuine mismatch and a conversation FromOperatorId once held that has
            // since been closed (Conversation.Close() does not clear OperatorId, so a stale command
            // replayed after a close would otherwise reach Conversation.TransferTo and fail there
            // instead, with a less specific message) - checked here, in the handler, not inside
            // Conversation.TransferTo itself. Same split CloseConversationHandler's own remarks draw
            // for Close: "is this caller the one assigned to this conversation" is a cross-aggregate,
            // permission-shaped fact (adr/0016), not an invariant only the aggregate can see.
            return ConversationErrors.Forbidden("This operator is not assigned to this conversation.");
        }

        var claimsCapacity = conversation.HoldsCapacityClaim;

        await using var transaction = await unitOfWork.BeginTransactionAsync(cancellationToken);

        if (claimsCapacity)
        {
            // Canonical order across the two `operators` rows this transaction touches, independent of
            // which side is claiming and which is releasing: whichever operator's id sorts first is
            // touched first, full stop. Without this, a transfer X->Y racing a transfer Y->X - the
            // "two transfers of the same conversation" scenario the backlog item's own Scope names,
            // and the same shape a swap between the same two operators would take - would each issue
            // "claim target, then release source" in program order: X->Y locks Y then X, Y->X locks X
            // then Y, a genuine self-inflicted inversion with nothing to do with the assignment
            // engine's own accepted, data-dependent cycle (`adr/0037`, this type's own remarks). Both
            // statements still commit or roll back together either way - this only decides which one
            // Postgres is asked to take a row lock for first.
            if (command.ToOperatorId.Value.CompareTo(command.FromOperatorId.Value) < 0)
            {
                if (!await capacity.TryClaimAsync(command.ToOperatorId, cancellationToken))
                {
                    // Refuse rather than queue - the backlog item's own Scope. Disposing the
                    // transaction without committing rolls back; nothing this attempt touched (nothing,
                    // in this branch - the claim was the first statement) survives.
                    return ConversationErrors.TransferTargetAtCapacity(command.ToOperatorId.Value);
                }

                await capacity.ReleaseAsync(command.FromOperatorId, cancellationToken);
            }
            else
            {
                await capacity.ReleaseAsync(command.FromOperatorId, cancellationToken);

                if (!await capacity.TryClaimAsync(command.ToOperatorId, cancellationToken))
                {
                    // The release above already ran, but this transaction never commits: disposing
                    // `transaction` without calling CommitAsync rolls back everything it did,
                    // including that release - the source operator's slot is exactly where it started.
                    return ConversationErrors.TransferTargetAtCapacity(command.ToOperatorId.Value);
                }
            }
        }

        try
        {
            conversation.TransferTo(command.ToOperatorId, clock.UtcNow);
        }
        catch (InvalidConversationStateException ex)
        {
            // The conversation stopped being Assigned to this operator between the read above and
            // here (closed by a racing close, most plausibly) - refused the same way
            // CloseConversationHandler refuses re-closing an already-closed row. The transaction is
            // disposed unsaved below, so any capacity call already made in this attempt rolls back
            // with it.
            return ConversationErrors.InvalidState(ex.Message);
        }

        var domainEvent = conversation.DomainEvents.OfType<ConversationTransferred>().Single();
        outbox.Enqueue(ConversationTransferredMapper.ToEnvelope(
            domainEvent, command.SiteId, conversation.VisitorId, idGenerator));
        conversation.ClearDomainEvents();

        // May throw ConversationConcurrencyConflictException (IConversationRepository's own contract,
        // `6-08`) - left to propagate to HandleAsync's retry loop. Runs inside this method's ambient
        // transaction exactly like the two capacity calls above: a conflict here aborts everything
        // this attempt did, the same as a deadlock on either capacity statement would.
        await conversations.SaveAsync(conversation, cancellationToken);

        await transaction.CommitAsync(cancellationToken);
        return Result.Success();
    }
}
