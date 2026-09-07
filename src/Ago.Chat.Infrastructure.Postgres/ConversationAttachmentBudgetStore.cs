using Ago.Chat.Application.Abstractions;
using Ago.Chat.Domain;
using Ago.Chat.Infrastructure.Postgres.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;

namespace Ago.Chat.Infrastructure.Postgres;

/// <summary>
/// `23-75`'s <see cref="IConversationAttachmentBudget"/> - raw SQL through the same
/// <see cref="AgoChatDbContext"/> connection <see cref="OperatorCapacityStore"/> and
/// <see cref="OperatorInviteRedemptionRepository"/> already use for exactly this reason: the
/// reservation only means anything if it lands in the same Postgres transaction as the
/// <c>attachments</c> row <c>CreateAttachmentHandler</c> is about to create, and EF's
/// <c>ExecuteSqlAsync</c>/raw-<see cref="NpgsqlCommand"/> family both participate in
/// <c>Database.CurrentTransaction</c> automatically once a caller has one open.
///
/// <para><b>A CTE, not <c>OperatorCapacityStore</c>'s bare <c>ExecuteSqlInterpolatedAsync</c>.</b>
/// That store's own compare-and-set never needs to report anything back beyond "did it happen" (a row
/// count); this one also has to hand a refused caller the real remaining budget
/// (<see cref="IConversationAttachmentBudget.TryReserveAsync"/>'s own contract, `23-75`'s own "the
/// refusal names the remaining budget" requirement) - so <see cref="TryReserveAsync"/> reaches for the
/// same "raw command through the ambient connection/transaction, read a value back" shape
/// <c>OperatorInviteRedemptionRepository.LockSiteAndReadSeatLimitAsync</c> already established, one
/// round trip rather than a compare-and-set followed by a second read that could see a different,
/// already-stale row (another writer's commit landing in the gap between the two reads).</para>
/// </summary>
public sealed class ConversationAttachmentBudgetStore(AgoChatDbContext db) : IConversationAttachmentBudget
{
    // `23-75`'s own fails-before: an earlier version of this statement computed the compare in the
    // `UPDATE`'s own `WHERE` (a bare `UPDATE ... WHERE reserved + @bytes <= @budget`, `OperatorCapacityStore`'s
    // own shape) and, on refusal, read the current total back with a *separate*, plain `SELECT` against
    // `conversations` with no lock of its own. Under real concurrent load that second read can see a
    // stale value: when the `UPDATE` blocks on this row's lock, Postgres's own re-check on the lock
    // releasing (`EvalPlanQual`) re-evaluates the `UPDATE`'s `WHERE` against the fresh, post-wait row -
    // but it does not change the snapshot the *rest* of the statement (an unrelated plain `SELECT` on
    // the same table) was already using. `ConversationAttachmentBudgetStoreTests`' own concurrency test
    // caught this directly: one refusal reported bytes as still available that a fresher read - the very
    // next statement - showed were not, because ten real concurrent connections raced the lock and this
    // one's "remaining" figure was computed from the row as it stood before it waited, not after.
    //
    // The fix is one read, not two: `current` takes the row's lock with `FOR UPDATE` first - which, like
    // the `UPDATE` before it, waits for a concurrent writer and then reads the *genuinely* fresh
    // post-wait value, this project's own established `SELECT ... FOR UPDATE` idiom
    // (`OperatorInviteRedemptionRepository.LockSiteAndReadSeatLimitAsync`, `WaitingConversationClaimQuery`)
    // - and computes the whole decision (`increment`: `@bytes` if it fits, `0` if it does not) from that
    // single locked read. `updated` applies exactly that increment; the final `SELECT` reports the real
    // post-write total and derives "was this reserved" from whether the increment was nonzero, never
    // from a second, independently-timed read of the row.
    private const string ReserveSql = """
        WITH current AS (
            SELECT
                attachment_bytes_reserved AS before,
                CASE WHEN attachment_bytes_reserved + @bytes <= @budgetBytes THEN @bytes ELSE 0 END AS increment
            FROM conversations
            WHERE id = @conversationId
            FOR UPDATE
        ),
        updated AS (
            UPDATE conversations
            SET attachment_bytes_reserved = attachment_bytes_reserved + (SELECT increment FROM current)
            WHERE id = @conversationId
            RETURNING attachment_bytes_reserved
        )
        SELECT updated.attachment_bytes_reserved, current.increment > 0
        FROM updated, current
        """;

    public async Task<AttachmentBudgetResult> TryReserveAsync(
        ConversationId conversationId, long bytes, long budgetBytes, CancellationToken cancellationToken)
    {
        await using var command = await CreateCommandAsync(ReserveSql, cancellationToken);
        command.Parameters.AddWithValue("conversationId", conversationId.Value);
        command.Parameters.AddWithValue("bytes", bytes);
        command.Parameters.AddWithValue("budgetBytes", budgetBytes);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            // AttachmentConfiguration.HasOne<Conversation>'s foreign key makes this unreachable in
            // production - CreateAttachmentHandler already loaded this exact conversation by id
            // before ever reaching this call.
            throw new InvalidOperationException(
                $"Conversation {conversationId.Value} was not found while reserving its attachment budget.");
        }

        var reservedTotal = reader.GetInt64(0);
        var reserved = reader.GetBoolean(1);
        // Floored at zero: if MaxConversationBytes is lowered by configuration after a conversation
        // already reserved more than the new ceiling, the arithmetic alone would go negative - the
        // same "never report a nonsensical negative" floor ReleaseAsync's own UPDATE applies to the
        // total itself.
        return new AttachmentBudgetResult(reserved, Math.Max(budgetBytes - reservedTotal, 0));
    }

    public async Task ReleaseAsync(ConversationId conversationId, long bytes, CancellationToken cancellationToken)
    {
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"""
            UPDATE conversations
            SET attachment_bytes_reserved = GREATEST(attachment_bytes_reserved - {bytes}, 0)
            WHERE id = {conversationId.Value}
            """,
            cancellationToken);
    }

    /// <summary>Opens the connection if this is the first statement issued through it this request -
    /// a caller-owned transaction (<c>IUnitOfWork.BeginTransactionAsync</c>) already opens it in
    /// production, but standalone callers (this class's own store-level tests) have not, and
    /// <see cref="TryReserveAsync"/> needs a result set back, which
    /// <c>Database.ExecuteSqlInterpolatedAsync</c> cannot give - so it builds the
    /// <see cref="NpgsqlCommand"/> itself rather than going through that helper.
    /// <c>Database.OpenConnectionAsync</c>, not the raw ADO connection's own <c>OpenAsync</c>: it
    /// participates in EF's own connection reference count, so it is safe to call even when a
    /// transaction already opened the connection - unlike calling <c>NpgsqlConnection.OpenAsync</c>
    /// directly, which would not know EF already considers it open.</summary>
    private async Task<NpgsqlCommand> CreateCommandAsync(string sql, CancellationToken cancellationToken)
    {
        await db.Database.OpenConnectionAsync(cancellationToken);
        var connection = (NpgsqlConnection)db.Database.GetDbConnection();
        var transaction = db.Database.CurrentTransaction?.GetDbTransaction() as NpgsqlTransaction;
        return new NpgsqlCommand(sql, connection, transaction);
    }
}
