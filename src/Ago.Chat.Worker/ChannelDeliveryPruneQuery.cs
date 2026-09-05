using Npgsql;

namespace Ago.Chat.Worker;

/// <summary>`23-19`: <c>WebhookDeliveryPruneQuery</c>'s own shape, applied to <c>channel_deliveries</c>.
/// No conflicting writer to avoid the way <c>OutboxDispatcher</c> conflicts with <c>OutboxPruneJob</c> -
/// nothing ever updates a <c>channel_deliveries</c> row after <c>ChannelDeliveryRepository.SaveAsync</c>
/// writes it (<c>ChannelDelivery</c>'s own remarks: never transitions) - but <c>FOR UPDATE SKIP LOCKED</c>
/// costs nothing and keeps this query's shape identical to its sibling rather than a special case
/// someone has to notice is different.</summary>
public static class ChannelDeliveryPruneQuery
{
    public static async Task<int> DeleteOlderThanBatchAsync(
        NpgsqlConnection connection, DateTimeOffset olderThan, int batchSize, CancellationToken cancellationToken)
    {
        const string sql = """
            DELETE FROM channel_deliveries
            WHERE id IN (
                SELECT id
                FROM channel_deliveries
                WHERE attempted_at < @olderThan
                ORDER BY attempted_at
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
