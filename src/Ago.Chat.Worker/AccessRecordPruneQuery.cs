using Npgsql;

namespace Ago.Chat.Worker;

/// <summary>`24-12`: <see cref="OutboxPruneQuery"/>/<see cref="WebhookDeliveryPruneQuery"/>'s own
/// shape, applied to <c>access_records</c> and keyed by <c>occurred_at</c> rather than
/// <c>created_at</c> - the same column name <c>AccessRecordEntity</c> already uses for when the access
/// happened. <c>FOR UPDATE SKIP LOCKED</c> costs nothing and keeps this query's shape identical to its
/// siblings, even though nothing else ever updates an access record after it is written (it is a
/// write-once row, the same as <c>webhook_deliveries</c>).</summary>
public static class AccessRecordPruneQuery
{
    public static async Task<int> DeleteOlderThanBatchAsync(
        NpgsqlConnection connection, DateTimeOffset olderThan, int batchSize, CancellationToken cancellationToken)
    {
        const string sql = """
            DELETE FROM access_records
            WHERE id IN (
                SELECT id
                FROM access_records
                WHERE occurred_at < @olderThan
                ORDER BY occurred_at
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
