using Ago.Chat.Application.Abstractions;
using Ago.Chat.Application.Mapping;
using Ago.Chat.Domain;
using Ago.Chat.Infrastructure.Postgres;
using Ago.Chat.Infrastructure.Postgres.Persistence;
using Ago.Platform.Caching.Redis;
using Ago.Platform.Kernel;
using Ago.Platform.Persistence.Postgres;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Ago.Chat.Worker;

/// <summary>
/// `4-03`'s `IAssignmentClaimer` mechanism B: `concurrency.md`'s "distributed lock in Redis, per
/// operator" alternative to `4-02`'s `SKIP LOCKED`. For each waiting conversation (a plain,
/// non-locking read - no Postgres row lock coordinates access here), tries each candidate operator
/// in least-loaded order until one's <see cref="RedisDistributedLock"/> is actually free.
///
/// The lock only ever decides who gets to *attempt* a claim - it does **not**, by itself, prevent
/// two replicas from both attempting the *same conversation* through two *different* operators'
/// locks simultaneously (nothing coordinates access to the conversation row the way `SKIP LOCKED`
/// does structurally). Correctness rests on two things underneath, exactly as `adr/0009`/`CLAUDE.md`
/// rule 8 require: `IOperatorCapacity`'s atomic compare-and-set (never over-claims a slot) and the
/// `Conversation` aggregate's own `xmin` optimistic-concurrency check on `SaveChangesAsync` - a
/// losing concurrent save throws `DbUpdateConcurrencyException`, caught here and treated exactly
/// like any other lost race. Both the capacity claim and the conversation save happen in one
/// Postgres transaction (`Database.UseTransactionAsync`, matching `SkipLockedAssignmentClaimer`'s
/// own reasoning) so a losing attempt's capacity claim rolls back with it - never a leaked slot.
///
/// Unlike mechanism A, a conversation whose top candidate's lock is currently held by someone else
/// tries the *next* candidate operator immediately, in the same attempt - lock contention is a
/// different, cheaper-to-retry situation than losing the capacity race itself (which still waits
/// for the next tick, same as mechanism A).
/// </summary>
public sealed class RedisLockAssignmentClaimer(
    RedisDistributedLock redisLock, NpgsqlDataSource dataSource, IClock clock, IIdGenerator idGenerator) : IAssignmentClaimer
{
    private static readonly TimeSpan LockTtl = TimeSpan.FromSeconds(5);

    public async Task<int> AssignWaitingConversationsAsync(SiteId siteId, int batchSize, CancellationToken cancellationToken)
    {
        var readOptions = new DbContextOptionsBuilder<AgoChatDbContext>().UseNpgsql(dataSource).Options;
        List<ConversationId> waitingIds;
        await using (var readDb = new AgoChatDbContext(readOptions))
        {
            waitingIds = await GetWaitingConversationIdsAsync(readDb, siteId, batchSize, cancellationToken);
        }

        var assignedCount = 0;
        foreach (var conversationId in waitingIds)
        {
            IReadOnlyList<OperatorId> candidates;
            await using (var readDb = new AgoChatDbContext(readOptions))
            {
                candidates = await GetCandidateOperatorsAsync(readDb, siteId, cancellationToken);
            }

            foreach (var operatorId in candidates)
            {
                await using var lockHandle = await redisLock.TryAcquireAsync(
                    $"assignment-lock:operator:{operatorId.Value:N}", LockTtl, cancellationToken);
                if (lockHandle is null)
                {
                    continue; // someone else holds it right now, or Redis is unreachable (fail-closed) - try the next candidate
                }

                if (await TryAssignAsync(conversationId, operatorId, cancellationToken))
                {
                    assignedCount++;
                    break;
                }
            }
        }

        return assignedCount;
    }

    private async Task<bool> TryAssignAsync(ConversationId conversationId, OperatorId operatorId, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var dbOptions = new DbContextOptionsBuilder<AgoChatDbContext>().UseNpgsql(connection).Options;
        await using var db = new AgoChatDbContext(dbOptions);
        await db.Database.UseTransactionAsync(transaction, cancellationToken);

        var capacity = new OperatorCapacityStore(db);
        if (!await capacity.TryClaimAsync(operatorId, cancellationToken))
        {
            await transaction.RollbackAsync(cancellationToken);
            return false;
        }

        var conversations = new ConversationRepository(db);
        var conversation = await conversations.GetByIdAsync(conversationId, cancellationToken);
        if (conversation is null || conversation.State != ConversationState.Waiting)
        {
            // Already taken (or closed) by a different replica's own attempt between our plain read
            // and now - roll back, which undoes the capacity claim above too since it shares this
            // same not-yet-committed transaction.
            await transaction.RollbackAsync(cancellationToken);
            return false;
        }

        try
        {
            // `6-09`: holdsCapacityClaim: true - same reasoning as SkipLockedAssignmentClaimer's own
            // call, and the same transaction rolls both back together if this save loses on `xmin`.
            var now = clock.UtcNow;
            conversation.AssignTo(operatorId, now, holdsCapacityClaim: true);

            // `23-03`: raw SQL, not IConversationAssignmentLog - ConversationAssignmentIntervalSql's
            // own remarks explain why both claimers are the deliberate exception to that port.
            await ConversationAssignmentIntervalSql.InsertOpenAsync(
                db, idGenerator, conversation.SiteId, conversationId, operatorId, ConversationAssignmentSource.Assigned,
                now, cancellationToken);

            var domainEvent = conversation.DomainEvents.OfType<ConversationAssigned>().Last();
            var outbox = new EfOutboxWriter<AgoChatDbContext>(db);
            outbox.Enqueue(ConversationAssignedToOperatorMapper.ToEnvelope(domainEvent, conversation.SiteId, conversation.VisitorId, idGenerator));
            conversation.ClearDomainEvents();
            await conversations.SaveAsync(conversation, cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            // xmin's own optimistic-concurrency check caught a genuine double-claim attempt - the
            // real correctness guard this mechanism relies on, not the Redis lock (see this type's
            // own remarks). Rolls back the capacity claim too.
            await transaction.RollbackAsync(cancellationToken);
            return false;
        }

        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    private static async Task<List<ConversationId>> GetWaitingConversationIdsAsync(
        AgoChatDbContext db, SiteId siteId, int batchSize, CancellationToken cancellationToken) =>
        await db.Conversations.AsNoTracking()
            .Where(c => c.SiteId == siteId && c.State == ConversationState.Waiting)
            .OrderBy(c => c.CreatedAt)
            .Take(batchSize)
            .Select(c => c.Id)
            .ToListAsync(cancellationToken);

    private static async Task<List<OperatorId>> GetCandidateOperatorsAsync(
        AgoChatDbContext db, SiteId siteId, CancellationToken cancellationToken) =>
        await db.Operators.AsNoTracking()
            .Where(o => o.SiteId == siteId && o.Status == OperatorStatus.Online)
            .Where(o => EF.Property<int>(o, "active_chats") < o.Capacity)
            .OrderBy(o => EF.Property<int>(o, "active_chats"))
            .Select(o => o.Id)
            .ToListAsync(cancellationToken);
}
