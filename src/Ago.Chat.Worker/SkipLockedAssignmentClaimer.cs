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
/// `4-02`/`4-03`'s `IAssignmentClaimer` mechanism A: `concurrency.md`'s "Operator assignment - the
/// contended path", `SELECT ... FOR UPDATE SKIP LOCKED`. Multiple `Worker` replicas run this at
/// once and never conflict on purpose - `SKIP LOCKED` means two replicas racing for the same
/// waiting conversation is a non-event, not a bug to avoid by coordination.
///
/// One Postgres transaction covers the whole batch: `4-01`'s `WaitingConversationClaimQuery` claims
/// up to `batchSize` waiting rows with their row locks held for the transaction's full lifetime,
/// then for each claimed conversation this finds a candidate operator and attempts
/// `IOperatorCapacity.TryClaimAsync` - all through one <see cref="AgoChatDbContext"/> built on the
/// *same* connection and transaction the claim itself used (`Database.UseTransactionAsync`), so a
/// claim and the assignment it enables commit or roll back together. A claimed conversation nobody
/// could be assigned to is simply left untouched - its lock releases when the transaction commits,
/// and it is claimable again on the very next tick.
///
/// Operator selection: least-`active_chats`-first among `Online` operators at the same site with
/// room, no `FOR UPDATE` (the atomic claim right after it is what makes the decision safe, not a
/// lock on the read) - an unmeasured ordering choice (`CLAUDE.md`), not specified upstream. If the
/// selected candidate loses the capacity race, this conversation simply stays `Waiting` for the
/// next tick rather than trying a second candidate - simpler, and still correct.
///
/// A batch assigning several claimed conversations to *different* operators holds more than one
/// `operators` row lock at once until it commits - two replicas' batches touching the same site's
/// operators in a different order can genuinely deadlock (Postgres detects it, `SqlState 40P01`).
/// Left for the caller to handle exactly like any other "lost the race" outcome - see
/// <see cref="ConversationAssignmentJob"/>'s own per-site catch.
///
/// <para>`23-05`: a second pass when the first finds no room at all -
/// <see cref="FindLeastActiveOnlineOperatorAsync"/> below, the identical `Status == Online` filter
/// <see cref="FindCandidateOperatorAsync"/> already uses with the capacity predicate dropped, tried
/// only once a conversation's own age (<c>Conversation.CreatedAt</c> against <see cref="IClock"/>,
/// never the database clock - `CLAUDE.md` rule 11) exceeds the site's own
/// <c>assignment_penalty_seconds</c>, read fresh inside this same transaction
/// (<see cref="SiteAssignmentPenaltyQuery"/>'s own remarks explain why never the cache). Claimed
/// through <c>IOperatorCapacity.ClaimAsync</c>, not <c>TryClaimAsync</c> - the whole point is that the
/// capacity compare must not be able to refuse this assignment. If no operator is `Online` at all,
/// neither pass finds a candidate and the conversation is simply left `Waiting` - `14-04`'s territory,
/// unchanged.</para>
/// </summary>
public sealed class SkipLockedAssignmentClaimer(NpgsqlDataSource dataSource, IClock clock, IIdGenerator idGenerator) : IAssignmentClaimer
{
    public async Task<int> AssignWaitingConversationsAsync(SiteId siteId, int batchSize, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        var claimedIds = await WaitingConversationClaimQuery.ClaimBatchAsync(
            connection, transaction, siteId, batchSize, cancellationToken);
        if (claimedIds.Count == 0)
        {
            await transaction.CommitAsync(cancellationToken);
            return 0;
        }

        var dbOptions = new DbContextOptionsBuilder<AgoChatDbContext>().UseNpgsql(connection).Options;
        await using var db = new AgoChatDbContext(dbOptions);
        await db.Database.UseTransactionAsync(transaction, cancellationToken);

        var conversations = new ConversationRepository(db);
        var capacity = new OperatorCapacityStore(db);
        var outbox = new EfOutboxWriter<AgoChatDbContext>(db);
        var now = clock.UtcNow;
        var assignedCount = 0;

        // `23-05`: fetched once for the whole batch, not per conversation - it is still read fresh,
        // inside this same transaction, every time this method runs (never cached across ticks), and
        // a batch of claimed rows is a short-lived unit that does not need to notice a penalty change
        // that lands mid-batch.
        int? penaltySeconds = null;

        foreach (var conversationId in claimedIds)
        {
            // Loaded, not just referenced by id: SKIP LOCKED claimed this row in this same
            // transaction, so it exists and is still Waiting by construction - never null here.
            var conversation = (await conversations.GetByIdAsync(conversationId, cancellationToken))!;

            var candidateOperatorId = await FindCandidateOperatorAsync(db, siteId, cancellationToken);
            OperatorId operatorId;
            ConversationAssignmentSource source;

            if (candidateOperatorId is { } roomCandidate)
            {
                if (!await capacity.TryClaimAsync(roomCandidate, cancellationToken))
                {
                    continue; // lost the race to another Worker replica's own transaction - retried next tick
                }

                operatorId = roomCandidate;
                source = ConversationAssignmentSource.Assigned;
            }
            else
            {
                // `23-05`: nobody has room right now. Assign anyway once this conversation has waited
                // past the site's own penalty - decisions.md §2: "a waiting customer is worse than
                // uneven load."
                penaltySeconds ??= await SiteAssignmentPenaltyQuery.GetSecondsAsync(db, siteId, cancellationToken);
                if (now - conversation.CreatedAt < TimeSpan.FromSeconds(penaltySeconds.Value))
                {
                    continue; // not old enough yet - stays Waiting, retried next tick
                }

                var overloadCandidateId = await FindLeastActiveOnlineOperatorAsync(db, siteId, cancellationToken);
                if (overloadCandidateId is not { } overloadCandidate)
                {
                    continue; // nobody Online at all - 14-04's territory, not this rule's
                }

                // Compare-free: the whole point of this pass is that capacity cannot refuse it.
                await capacity.ClaimAsync(overloadCandidate, cancellationToken);
                operatorId = overloadCandidate;
                source = ConversationAssignmentSource.Additional;
            }

            // `6-09`: holdsCapacityClaim: true - the slot was actually taken, one statement ago, in
            // this same transaction, whichever pass took it. The receipt commits with the assignment,
            // so a conversation is never `Assigned` with a claim behind it and no record of one (nor
            // the reverse), and CloseConversationHandler has something exact to hand back on close.
            conversation.AssignTo(operatorId, now, holdsCapacityClaim: true);

            // `23-03`/`23-05`: raw SQL, not IConversationAssignmentLog - see
            // ConversationAssignmentIntervalSql's own remarks for why this claimer is one of the two
            // deliberate exceptions to that port. `source` carries which pass actually assigned it.
            await ConversationAssignmentIntervalSql.InsertOpenAsync(
                db, idGenerator, siteId, conversationId, operatorId, source, now, cancellationToken);

            var domainEvent = conversation.DomainEvents.OfType<ConversationAssigned>().Last();
            outbox.Enqueue(ConversationAssignedToOperatorMapper.ToEnvelope(domainEvent, siteId, conversation.VisitorId, idGenerator));
            conversation.ClearDomainEvents();

            await conversations.SaveAsync(conversation, cancellationToken);
            assignedCount++;
        }

        await transaction.CommitAsync(cancellationToken);
        return assignedCount;
    }

    private static async Task<OperatorId?> FindCandidateOperatorAsync(
        AgoChatDbContext db, SiteId siteId, CancellationToken cancellationToken) =>
        await db.Operators.AsNoTracking()
            .Where(o => o.SiteId == siteId && o.Status == OperatorStatus.Online)
            .Where(o => EF.Property<int>(o, "active_chats") < o.Capacity)
            .OrderBy(o => EF.Property<int>(o, "active_chats"))
            .Select(o => (OperatorId?)o.Id)
            .FirstOrDefaultAsync(cancellationToken);

    /// <summary>`23-05`: <see cref="FindCandidateOperatorAsync"/> with the capacity predicate
    /// dropped - the identical `Status == Online` filter, deliberately not re-derived, so an `Away`
    /// (or `Offline`) operator is excluded from this pass for exactly the same reason it is excluded
    /// from the first: there is one `Online` filter in this file, not two that could drift apart.
    /// </summary>
    private static async Task<OperatorId?> FindLeastActiveOnlineOperatorAsync(
        AgoChatDbContext db, SiteId siteId, CancellationToken cancellationToken) =>
        await db.Operators.AsNoTracking()
            .Where(o => o.SiteId == siteId && o.Status == OperatorStatus.Online)
            .OrderBy(o => EF.Property<int>(o, "active_chats"))
            .Select(o => (OperatorId?)o.Id)
            .FirstOrDefaultAsync(cancellationToken);
}
