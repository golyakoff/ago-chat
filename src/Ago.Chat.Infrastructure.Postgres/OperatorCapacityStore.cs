using Ago.Chat.Application.Abstractions;
using Ago.Chat.Contracts;
using Ago.Chat.Domain;
using Ago.Chat.Infrastructure.Postgres.Persistence;
using Microsoft.EntityFrameworkCore;

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
        var rowsAffected = await db.Database.ExecuteSqlInterpolatedAsync(
            $"""
            UPDATE operators
            SET active_chats = active_chats + 1
            WHERE id = {operatorId.Value} AND active_chats < capacity
            """,
            cancellationToken);

        // `7-02`: nfr.md's "assignment attempts vs conflicts" - counted here, the single choke point
        // both IAssignmentClaimer implementations (SkipLockedAssignmentClaimer, RedisLockAssignmentClaimer)
        // call through, rather than in either caller separately. concurrency.md's own words: a rows-
        // affected count of 0 is "a normal outcome to retry, not an error to log at Error level" -
        // this item counts it instead of only logging it at Debug, exactly as the backlog item asks.
        var claimed = rowsAffected > 0;
        ChatMetrics.RecordCapacityClaimAttempt(claimed);
        return claimed;
    }

    public async Task ReleaseAsync(OperatorId operatorId, CancellationToken cancellationToken)
    {
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"""
            UPDATE operators
            SET active_chats = active_chats - 1
            WHERE id = {operatorId.Value} AND active_chats > 0
            """,
            cancellationToken);
    }
}
