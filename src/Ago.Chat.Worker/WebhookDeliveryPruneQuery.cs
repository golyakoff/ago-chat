using Npgsql;

namespace Ago.Chat.Worker;

/// <summary>`15-04`: <see cref="OutboxPruneQuery"/>'s own shape, applied to <c>webhook_deliveries</c>.
/// No <c>FOR UPDATE SKIP LOCKED</c> conflict to avoid here the way outbox rows conflict with
/// <c>OutboxDispatcher</c> - nothing else ever updates a `webhook_deliveries` row after it is written
/// (`WebhookDelivery`'s own remarks: one summary row per delivery, not a state machine) - but the
/// clause costs nothing and keeps this query's shape identical to its sibling rather than a special
/// case someone has to notice is different.</summary>
public static class WebhookDeliveryPruneQuery
{
    public static async Task<int> DeleteOlderThanBatchAsync(
        NpgsqlConnection connection, DateTimeOffset olderThan, int batchSize, CancellationToken cancellationToken)
    {
        const string sql = """
            DELETE FROM webhook_deliveries
            WHERE id IN (
                SELECT id
                FROM webhook_deliveries
                WHERE created_at < @olderThan
                ORDER BY created_at
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
