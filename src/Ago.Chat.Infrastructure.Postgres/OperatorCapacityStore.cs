using Ago.Chat.Application.Abstractions;
using Ago.Chat.Contracts;
using Ago.Chat.Domain;
using Ago.Chat.Infrastructure.Postgres.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Ago.Chat.Infrastructure.Postgres;

/// <summary>
/// `4-01`'s `IOperatorCapacity` - raw SQL, not LINQ (the atomic compare-and-set has no LINQ shape),
/// but issued through <see cref="AgoChatDbContext"/>'s own connection via
/// <c>ExecuteSqlInterpolatedAsync</c> rather than a separate <c>NpgsqlDataSource</c> connection.
///
/// `4-02` needs this: a claim and the conversation assignment it enables must commit atomically -
/// crash or fail between the two and a capacity slot leaks forever, permanently invisible to any
/// future claim query, with no assigned conversation to account for it. EF's <c>ExecuteSqlAsync</c>
/// family participates in <c>Database.CurrentTransaction</c> automatically when the caller has one
/// open (exactly what `4-02`'s per-batch transaction needs), and still works standalone with its own
/// implicit transaction when there is none (exactly what `4-01`'s original, still-standalone tests
/// need) - one implementation serves both without the port itself changing shape.
/// </summary>
public sealed class OperatorCapacityStore(AgoChatDbContext db) : IOperatorCapacity
{
    public async Task<bool> TryClaimAsync(OperatorId operatorId, CancellationToken cancellationToken)
    {
        int rowsAffected;
        try
        {
            rowsAffected = await db.Database.ExecuteSqlInterpolatedAsync(
                $"""
                UPDATE operators
                SET active_chats = active_chats + 1
                WHERE id = {operatorId.Value} AND active_chats < capacity
                """,
                cancellationToken);
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.DeadlockDetected)
        {
            // `18-02`: this port's own doc comment on <see cref="OperatorCapacityContentionException"/>
            // used to say "TryClaimAsync gets no retry - every call to it in production is inside a
            // claimer's batch transaction" (`adr/0037`, point 5), and that stays true here: no retry is
            // added. What changed is that a *second* kind of caller-owned transaction now exists -
            // TransferConversationHandler's own (Ago.Chat.Application.UseCases.TransferConversation) -
            // and unlike SkipLockedAssignmentClaimer/RedisLockAssignmentClaimer, it is a per-request
            // Application handler that must never see a raw Npgsql exception (CLAUDE.md rule 2). Before
            // this item, a 40P01 here propagated as PostgresException because the only two callers
            // (both in Ago.Chat.Worker, a host) were allowed to know that; the moment
            // Ago.Chat.Application got its first caller, that leak became a real dependency-rule
            // violation, not a hypothetical one. Translating it here - the same port-boundary
            // translation ReleaseAsync's caller-owned-transaction branch already does - fixes the leak
            // for both callers at once, and changes nothing observable for the existing two: their own
            // catch (Exception ex) when (ex is not OperationCanceledException) at ConversationAssignmentJob
            // and each RunOnceAsync call site catches this exactly as it caught the raw
            // PostgresException before, logs at the same level, and retries next tick.
            //
            // Attempts is always 1: a deadlock inside a caller-owned transaction has already aborted
            // it, so - exactly as ReleaseAsync's own remarks explain - there is no statement-level
            // retry to make here. The retry unit, if the caller wants one, is its own transaction.
            //
            // Deliberately not ChatMetrics.RecordCapacityReleaseDeadlock: that counter's own
            // description names ReleaseAsync specifically, and a claim-side deadlock tagged onto it
            // would misreport which statement Postgres actually aborted. A dedicated claim-deadlock
            // instrument is real observability work this item leaves undone - Stage 7's own job
            // (CLAUDE.md's roadmap references), not silently bolted onto an existing counter whose
            // documented meaning would then be wrong.
            throw new OperatorCapacityContentionException(operatorId, attempts: 1, ex);
        }

        // `7-02`: nfr.md's "assignment attempts vs conflicts" - counted here, the single choke point
        // both IAssignmentClaimer implementations (SkipLockedAssignmentClaimer, RedisLockAssignmentClaimer)
        // call through, rather than in either caller separately. concurrency.md's own words: a rows-
        // affected count of 0 is "a normal outcome to retry, not an error to log at Error level" -
        // this item counts it instead of only logging it at Debug, exactly as the backlog item asks.
        var claimed = rowsAffected > 0;
        ChatMetrics.RecordCapacityClaimAttempt(claimed);
        return claimed;
    }

    /// <summary>
    /// `23-04`: the compare-free claim - see <see cref="IOperatorCapacity.ClaimAsync"/>'s own remarks
    /// for why this exists beside <see cref="TryClaimAsync"/> rather than a parameter on it. Same
    /// deadlock translation as <see cref="TryClaimAsync"/>, same reasoning for no retry here: every
    /// production caller (<c>AssignConversationHandler</c>) runs this inside its own explicit
    /// <c>IUnitOfWork</c> transaction, which a `40P01` has already aborted in full - there is nothing
    /// left on this connection to safely re-issue, so the retry unit is the caller's whole transaction,
    /// not this one statement (the identical split <see cref="TryClaimAsync"/>'s own remarks describe).
    /// Deliberately does not call <see cref="Ago.Chat.Contracts.ChatMetrics.RecordCapacityClaimAttempt"/>
    /// - that counter's own description is specifically about a compare that can lose, and reusing it
    /// here would misreport a call that never loses the compare (only, rarely, the statement itself) as
    /// a claim attempt with an outcome to weigh against the ones that can actually be refused.
    /// </summary>
    public async Task ClaimAsync(OperatorId operatorId, CancellationToken cancellationToken)
    {
        try
        {
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"""
                UPDATE operators
                SET active_chats = active_chats + 1
                WHERE id = {operatorId.Value}
                """,
                cancellationToken);
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.DeadlockDetected)
        {
            throw new OperatorCapacityContentionException(operatorId, attempts: 1, ex);
        }
    }

    /// <summary>
    /// `6-10`: the release retries on `40P01`, the claim does not. Not an oversight - the two calls
    /// sit in different transaction shapes and only one of them *can* retry. Every
    /// <see cref="TryClaimAsync"/> in production runs inside an <see cref="IAssignmentClaimer"/>'s
    /// batch transaction, which the deadlock has already aborted: the next statement on that
    /// connection could only fail with `25P02 in_failed_sql_transaction`, so the retry unit there is
    /// the whole batch, and <c>ConversationAssignmentJob</c> has caught and re-run it next tick since
    /// `4-02`. The close's release runs in no transaction at all, so re-issuing the single statement
    /// is both possible and exactly correct.
    /// </summary>
    private const int ReleaseAttempts = 5;

    public async Task ReleaseAsync(OperatorId operatorId, CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await db.Database.ExecuteSqlInterpolatedAsync(
                    $"""
                    UPDATE operators
                    SET active_chats = active_chats - 1
                    WHERE id = {operatorId.Value} AND active_chats > 0
                    """,
                    cancellationToken);
                return;
            }
            catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.DeadlockDetected)
            {
                // `6-10`, and the reason this catch exists at all: a *single-row* `UPDATE` in its own
                // implicit transaction looks incapable of deadlocking, and is not. Before it waits on
                // the row's current updater it takes a heavyweight tuple lock on that row as its place
                // in the queue - and while it holds that place, an assignment batch already holding a
                // *different* operators row can queue behind it, closing a cycle this statement had no
                // locks of its own to create. The captured graph is in `6-10`'s backlog item: four
                // participants, every one of them `UPDATE operators SET active_chats = +/- 1`, and the
                // victim Postgres picked was the innocent single-statement release. Retrying is
                // correct rather than merely convenient: the aborted transaction applied nothing, so
                // the re-issued statement is the first and only decrement of that slot.
                var canRetry = db.Database.CurrentTransaction is null && attempt < ReleaseAttempts;
                ChatMetrics.RecordCapacityReleaseDeadlock(retried: canRetry);
                if (!canRetry)
                {
                    throw new OperatorCapacityContentionException(operatorId, attempt, ex);
                }

                // Jittered, and growing, for the ordinary thundering-herd reason: a detected cycle
                // usually has several releases queued on the same row, and Postgres aborts one of
                // them - re-issuing them all in lockstep is how the next cycle gets built. The bound
                // is argued in `adr/0037`: each further attempt needs an independent coincidence, and
                // beyond a handful of milliseconds the trade turns into "make an operator wait longer"
                // for a slot the disconnect sweep already recovers.
                await Task.Delay(TimeSpan.FromMilliseconds(Random.Shared.Next(4, 16) * attempt), cancellationToken);
            }
        }
    }
}
