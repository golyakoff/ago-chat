using Npgsql;

namespace Ago.Chat.Worker;

/// <summary>
/// `15-04`: the bounded-batch delete half of outbox pruning, following
/// <see cref="AttachmentOrphanSweepQuery"/>'s own raw-Npgsql-in-the-Worker shape - `DELETE ... WHERE id
/// IN (SELECT ... LIMIT ... FOR UPDATE SKIP LOCKED)` rather than a bare `DELETE ... WHERE ... LIMIT`,
/// because Postgres's `DELETE` has no `LIMIT` clause of its own; the subselect is what bounds it.
/// `FOR UPDATE SKIP LOCKED` means this job never blocks on - or steals - a row `OutboxDispatcher` is
/// mid-transaction on, the same reason `AttachmentOrphanSweepQuery` takes it.
/// </summary>
public static class OutboxPruneQuery
{
    public static async Task<int> DeletePublishedBatchAsync(
        NpgsqlConnection connection, DateTimeOffset olderThan, int batchSize, CancellationToken cancellationToken)
    {
        const string sql = """
            DELETE FROM outbox
            WHERE id IN (
                SELECT id
                FROM outbox
                WHERE published_at IS NOT NULL AND published_at < @olderThan
                ORDER BY published_at
                LIMIT @batchSize
                FOR UPDATE SKIP LOCKED
            )
            """;

        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("olderThan", olderThan);
        command.Parameters.AddWithValue("batchSize", batchSize);

        return await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
