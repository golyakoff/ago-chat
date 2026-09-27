using Ago.Chat.Application.Abstractions;
using Ago.Chat.Application.Mapping;
using Ago.Chat.Domain;
using Ago.Chat.Infrastructure.Postgres;
using Ago.Chat.Infrastructure.Postgres.Persistence;
using Ago.Platform.Kernel;
using Ago.Platform.Persistence.Postgres;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Ago.Chat.Worker;

/// <summary>
/// `4-04`: releases every conversation currently `Assigned` to one operator back to `Waiting`, and
/// releases their capacity - one Postgres transaction covering all of it, so a failure partway
/// through leaks nothing (matching `SkipLockedAssignmentClaimer`/`RedisLockAssignmentClaimer`'s own
/// reasoning: `IOperatorCapacity.ReleaseAsync` and each `Conversation.SaveAsync` must commit
/// together, or a crash between them either leaks a phantom-occupied slot forever or frees capacity
/// with no record of which conversation it belonged to).
///
/// <para>`26-238`: the same transaction also takes the operator `Offline` when it released at least one
/// conversation - see the flip below `ReleaseAllAsync`'s loop for why leaving `operators.status` at
/// `Online` after deciding the operator is gone is what let the assignment engine and this release path
/// fight in an unbounded loop, churning `conversation_assignments` intervals.</para>
/// </summary>
public sealed class OperatorConversationReleaser(NpgsqlDataSource dataSource, IClock clock, IIdGenerator idGenerator)
{
    public async Task<int> ReleaseAllAsync(OperatorId operatorId, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var dbOptions = new DbContextOptionsBuilder<AgoChatDbContext>().UseNpgsql(connection).Options;
        await using var db = new AgoChatDbContext(dbOptions);
        await db.Database.UseTransactionAsync(transaction, cancellationToken);

        var conversations = new ConversationRepository(db);
        var capacity = new OperatorCapacityStore(db);
        var operators = new OperatorRepository(db);
        var outbox = new EfOutboxWriter<AgoChatDbContext>(db);
        // `23-03`: uses the port (unlike the two claimers - see ConversationAssignmentIntervalSql's own
        // remarks on why they do not), the same "instantiate directly, sharing this batch's own db"
        // shape this method already uses for OperatorCapacityStore right above. `26-237`: it now also
        // stages the ConversationAssignmentClosed analytics event onto this same `db`/`outbox` at close,
        // so it is constructed with the batch's own outbox and id generator plus the pooled `dataSource`
        // for its read-only overlap/reply lookups - the event rides this transaction's own commit below.
        IConversationAssignmentLog assignmentLog = new ConversationAssignmentLog(db, dataSource, outbox, idGenerator);
        var now = clock.UtcNow;

        var assigned = await conversations.GetAssignedToOperatorAsync(operatorId, cancellationToken);
        foreach (var conversation in assigned)
        {
            var siteId = conversation.SiteId;
            var visitorId = conversation.VisitorId;
            var consumedCapacityClaim = conversation.ReleaseToQueue(now);

            // `23-03`: closes without opening - one of the two writers `23-03`'s own Scope names for
            // this exact shape (the other is CloseConversationHandler). Staged on the same `db` this
            // conversation's own SaveAsync flushes below, same as every other writer.
            await assignmentLog.CloseOpenAsync(conversation.Id, now, cancellationToken);

            var domainEvent = conversation.DomainEvents.OfType<ConversationReleased>().Last();
            outbox.Enqueue(ConversationReleasedToQueueMapper.ToEnvelope(domainEvent, siteId, visitorId, idGenerator));
            conversation.ClearDomainEvents();

            await conversations.SaveAsync(conversation, cancellationToken);

            // `6-09`: conditional, where this used to decrement once per assigned conversation
            // unconditionally - which asks for more decrements than there were claims whenever the
            // operator picked a conversation up by hand (AssignConversationHandler takes no capacity
            // claim at all). This sweep happened to survive that because it releases *every* one of
            // the operator's assignments and ReleaseAsync floors at zero, so the extra decrements had
            // nothing left to eat. Changed anyway, and deliberately: "one release per claim" is now a
            // rule CloseConversationHandler depends on, and a second path quietly obeying a different
            // rule that only agrees by accident is how the two drift apart later. The receipt is the
            // rule; the floor is a backstop, not the mechanism.
            //
            // Not replaced by a flat `SET active_chats = 0` for this operator, which looks like the
            // stronger repair and is actually unsafe: the assigned-conversation list above was read
            // before this transaction touched the operators row, so a claim another Worker replica
            // committed in between belongs to a conversation this sweep will not release, and zeroing
            // the counter would strand it.
            if (consumedCapacityClaim)
            {
                await capacity.ReleaseAsync(operatorId, cancellationToken);
            }
        }

        // `26-238`: having decided this operator is gone (this method only ever runs once a caller has
        // established that - the disconnect-grace consumer, after a full `GracePeriod` with zero live
        // connections; the operator-removed consumer, permanently), take them `Offline` in this same
        // transaction, so `operators.status` agrees with the release we just performed.
        //
        // Why this is a bug fix and not a cosmetic tidy-up. Assignment reads `operators.status`
        // (`SkipLockedAssignmentClaimer`/`RedisLockAssignmentClaimer` filter `Status == Online`); release
        // reads the connection registry (`OperatorDisconnectSweepJob`/`OperatorDisconnectGraceConsumer`).
        // When those two disagree - an operator left `Online` in the row but with no live connection,
        // which is exactly what an ungraceful `Ago.Chat.Api` shutdown leaves behind, since
        // `OperatorHub.OnDisconnectedAsync`'s `Operator.GoOffline` never ran - the engine re-hands the
        // just-released conversation straight back to the phantom operator, the sweep releases it again a
        // grace period later, and the two subsystems fight forever, writing a fresh
        // `conversation_assignments` interval on every ~`GracePeriod` cycle with no user action at all.
        // That unattended metronome is what accrued ~1100 intervals for 9 conversations on the stand
        // (`26-238`). Reconciling `Status` here is the seam that breaks it: an `Offline` operator is no
        // longer an assignment candidate, so the conversation stays `Waiting` for a real operator instead
        // of ping-ponging on a dead one. A genuine reconnect still flips them back `Online`
        // (`Operator.NoteConnected`, `OperatorHub.OnConnectedAsync`) and a real reassignment then records
        // a real new interval - the legitimate case is untouched.
        //
        // Gated on `assigned.Count > 0`: the loop we are breaking only exists for an operator holding
        // assigned conversations (the sweep only ever targets those), so there is nothing to reconcile
        // when the release found none - and skipping the extra load/write in that case also avoids
        // touching the row of an operator whose disconnect fast path already set them `Offline`.
        // `Operator.GoOffline` leaves a deliberate `Away` alone (its own remarks), so an operator who
        // stepped away and then lost their connection stays `Away` - itself already excluded from
        // assignment, so the loop cannot form for them either.
        if (assigned.Count > 0)
        {
            var operatorEntity = await operators.GetByIdAsync(operatorId, cancellationToken);
            if (operatorEntity is not null)
            {
                operatorEntity.GoOffline();
                await operators.SaveAsync(operatorEntity, cancellationToken);
            }
        }

        await transaction.CommitAsync(cancellationToken);
        return assigned.Count;
    }
}
