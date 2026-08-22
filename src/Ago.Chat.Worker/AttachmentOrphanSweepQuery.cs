using Ago.Chat.Domain;
using Npgsql;

namespace Ago.Chat.Worker;

/// <summary>
/// `5-04`: an atomic compare-and-delete, not a load-then-decide - one `DELETE ... WHERE state =
/// 'Pending' ... RETURNING` statement, following <see cref="WaitingConversationClaimQuery"/>'s own
/// raw-Npgsql-in-the-Worker shape for exactly this kind of claim. This single statement *is* the
/// ordering guarantee `5-04`'s Done-when asks for: Postgres evaluates the inner `WHERE state =
/// 'Pending'` against the row's committed state at the moment this statement executes, so an
/// attachment confirmed (state flipped to `Ready`) by a transaction that commits before this one runs
/// is already excluded - there is no separate "select candidates" step for a race to land inside.
/// `FOR UPDATE SKIP LOCKED` in the inner subquery additionally means a row a concurrent confirm is
/// still in the middle of updating (locked, not yet committed) is skipped outright rather than making
/// this statement wait on it - proven directly in <c>AttachmentOrphanSweepJobTests</c>.
/// </summary>
public static class AttachmentOrphanSweepQuery
{
    public static async Task<IReadOnlyList<(AttachmentId Id, string ObjectKey)>> ClaimExpiredPendingBatchAsync(
        NpgsqlConnection connection, DateTimeOffset olderThan, int batchSize, CancellationToken cancellationToken)
    {
        const string sql = """
            DELETE FROM attachments
            WHERE id IN (
                SELECT id
                FROM attachments
                WHERE state = 'Pending' AND created_at < @olderThan
                ORDER BY created_at
                LIMIT @batchSize
                FOR UPDATE SKIP LOCKED
            )
            RETURNING id, object_key
            """;

        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("olderThan", olderThan);
        command.Parameters.AddWithValue("batchSize", batchSize);

        var claimed = new List<(AttachmentId, string)>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            claimed.Add((new AttachmentId(reader.GetGuid(0)), reader.GetString(1)));
        }

        return claimed;
    }
}
