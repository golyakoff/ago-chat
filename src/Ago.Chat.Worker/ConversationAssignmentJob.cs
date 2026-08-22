using Ago.Chat.Application.Mapping;
using Ago.Chat.Domain;
using Ago.Chat.Infrastructure.Postgres;
using Ago.Chat.Infrastructure.Postgres.Persistence;
using Ago.Platform.Abstractions;
using Ago.Platform.Kernel;
using Ago.Platform.Persistence.Postgres;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Ago.Chat.Worker;

/// <summary>
/// `4-02`: `concurrency.md`'s "Operator assignment - the contended path", mechanism A
/// (`SELECT ... FOR UPDATE SKIP LOCKED`). Multiple `Worker` replicas run this loop at once and
/// never conflict on purpose - `SKIP LOCKED` means two replicas racing for the same waiting
/// conversation is a non-event, not a bug to avoid by coordination.
///
/// Per site, per tick, one Postgres transaction covers the whole batch: `4-01`'s
/// `WaitingConversationClaimQuery` claims up to `BatchSize` waiting rows with their row locks held
/// for the transaction's full lifetime, then for each claimed conversation this job finds a
/// candidate operator and attempts `IOperatorCapacity.TryClaimAsync` - all through one
/// <see cref="AgoChatDbContext"/> built on the *same* connection and transaction the claim itself
/// used (`Database.UseTransactionAsync`), so a claim and the assignment it enables commit or roll
/// back together. A claimed conversation nobody could be assigned to is simply left untouched -
/// its lock releases when the transaction commits, and it is claimable again on the very next tick,
/// exactly as `4-01`'s own reasoning describes.
///
/// Operator selection: least-`active_chats`-first among `Online` operators at the same site with
/// room, no `FOR UPDATE` (the atomic claim right after it is what makes the decision safe, not a
/// lock on the read) - an unmeasured ordering choice (`CLAUDE.md`), not specified upstream. If the
/// selected candidate loses the capacity race (another Worker replica's own transaction claimed it
/// first, in the gap between this job's read and its own claim), this conversation simply stays
/// `Waiting` for the next tick rather than trying a second candidate - simpler, and still correct.
/// </summary>
public sealed class ConversationAssignmentJob(
    NpgsqlDataSource dataSource,
    IClock clock,
    IIdGenerator idGenerator,
    IOptions<ConversationAssignmentJobOptions> options,
    ILogger<ConversationAssignmentJob> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(options.Value.Interval);
        do
        {
            try
            {
                await RunOnceAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // concurrency.md: a BackgroundService catches and continues - a transient Postgres
                // blip here must not permanently kill the assignment loop.
                logger.LogError(ex, "Assignment cycle failed; retrying next cycle.");
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken)); // runs once immediately, then every Interval
    }

    internal async Task RunOnceAsync(CancellationToken cancellationToken)
    {
        foreach (var siteId in await GetSiteIdsWithWaitingConversationsAsync(cancellationToken))
        {
            try
            {
                await AssignBatchForSiteAsync(siteId, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // A batch that assigns several claimed conversations to *different* operators holds
                // more than one operators row lock at once (each TryClaimAsync) until it commits -
                // two replicas' batches touching the same site's operators in a different order can
                // genuinely deadlock (Postgres detects and aborts one side, SqlState 40P01). This is
                // exactly as normal as TryClaimAsync itself returning false - concurrency.md's "not
                // an error to log at Error level" extended to the transaction level - and one site's
                // contention must not stall every other site this tick.
                logger.LogDebug(ex, "Assignment batch for site {SiteId} failed this tick; retrying next tick.", siteId);
            }
        }
    }

    private async Task<IReadOnlyList<SiteId>> GetSiteIdsWithWaitingConversationsAsync(CancellationToken cancellationToken)
    {
        const string sql = "SELECT DISTINCT site_id FROM conversations WHERE state = 'Waiting'";

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);

        var siteIds = new List<SiteId>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            siteIds.Add(new SiteId(reader.GetGuid(0)));
        }

        return siteIds;
    }

    private async Task AssignBatchForSiteAsync(SiteId siteId, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        var claimedIds = await WaitingConversationClaimQuery.ClaimBatchAsync(
            connection, transaction, siteId, options.Value.BatchSize, cancellationToken);
        if (claimedIds.Count == 0)
        {
            await transaction.CommitAsync(cancellationToken);
            return;
        }

        var dbOptions = new DbContextOptionsBuilder<AgoChatDbContext>().UseNpgsql(connection).Options;
        await using var db = new AgoChatDbContext(dbOptions);
        await db.Database.UseTransactionAsync(transaction, cancellationToken);

        var conversations = new ConversationRepository(db);
        var capacity = new OperatorCapacityStore(db);
        var outbox = new EfOutboxWriter<AgoChatDbContext>(db);
        var now = clock.UtcNow;

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
            conversation.AssignTo(operatorId, now);

            var domainEvent = conversation.DomainEvents.OfType<ConversationAssigned>().Last();
            outbox.Enqueue(ConversationAssignedToOperatorMapper.ToEnvelope(domainEvent, siteId, conversation.VisitorId, idGenerator));
            conversation.ClearDomainEvents();

            await conversations.SaveAsync(conversation, cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
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
