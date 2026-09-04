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

        foreach (var conversationId in claimedIds)
        {
            var candidateOperatorId = await FindCandidateOperatorAsync(db, siteId, cancellationToken);
            if (candidateOperatorId is not { } operatorId)
            {
                continue; // no operator has room right now - stays Waiting, retried next tick
            }

            if (!await capacity.TryClaimAsync(operatorId, cancellationToken))
            {
                continue; // lost the race to another Worker replica's own transaction - retried next tick
            }

            // Loaded, not just referenced by id: SKIP LOCKED claimed this row in this same
            // transaction, so it exists and is still Waiting by construction - never null here.
            var conversation = (await conversations.GetByIdAsync(conversationId, cancellationToken))!;
            // `6-09`: holdsCapacityClaim: true - the slot was actually taken, one statement ago, in
            // this same transaction. The receipt commits with the assignment, so a conversation is
            // never `Assigned` with a claim behind it and no record of one (nor the reverse), and
            // CloseConversationHandler has something exact to hand back on close.
            conversation.AssignTo(operatorId, now, holdsCapacityClaim: true);

            // `23-03`: raw SQL, not IConversationAssignmentLog - see ConversationAssignmentIntervalSql's
            // own remarks for why this claimer is one of the two deliberate exceptions to that port.
            await ConversationAssignmentIntervalSql.InsertOpenAsync(
                db, idGenerator, siteId, conversationId, operatorId, ConversationAssignmentSource.Assigned, now,
                cancellationToken);

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
}
