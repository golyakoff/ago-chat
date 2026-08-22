using Ago.Chat.Domain;
using Npgsql;

namespace Ago.Chat.Worker;

/// <summary>
/// `4-01`: the waiting-queue claim `4-02`'s assignment engine will loop over -
/// <c>SELECT ... FOR UPDATE SKIP LOCKED</c>, raw Npgsql in the Worker host, following
/// <see cref="OutboxDispatcher"/>'s own established shape for exactly this SQL pattern
/// (<see cref="ClaimedOutboxRow"/>). Deliberately not a Dapper read behind
/// <c>IConversationReadStore</c>: a <c>SKIP LOCKED</c> claim only makes sense inside the same
/// transaction the caller commits or rolls back - the row lock it takes is released the instant
/// that transaction ends, whether or not the caller actually assigned anything, which is exactly
/// what lets a claimed-but-unassignable conversation go back to being visible on the very next
/// tick without any explicit "unclaim" step.
///
/// No caller yet - `4-02`'s assignment loop is the real one. Tested standalone here (real
/// concurrent transactions, not sequential awaits) because the guarantee this exists to prove -
/// two transactions racing the same site's queue split the rows, never double-claim one - does not
/// need a caller to be true or to be checked.
/// </summary>
public static class WaitingConversationClaimQuery
{
    public static async Task<IReadOnlyList<ConversationId>> ClaimBatchAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, SiteId siteId, int batchSize, CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT id
            FROM conversations
            WHERE site_id = @siteId AND state = 'Waiting'
            ORDER BY created_at
            LIMIT @batchSize
            FOR UPDATE SKIP LOCKED
            """;

        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("siteId", siteId.Value);
        command.Parameters.AddWithValue("batchSize", batchSize);

        var claimed = new List<ConversationId>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            claimed.Add(new ConversationId(reader.GetGuid(0)));
        }

        return claimed;
    }
}
