using Npgsql;

namespace Ago.Chat.Worker;

/// <summary>
/// `15-04`: <c>inbox</c> is keyed <c>(message_id, consumer)</c> (`Ago.Platform.Persistence.Postgres.
/// InboxRecordConfiguration`) - a composite primary key, unlike <c>outbox</c>'s bare <c>id</c> and
/// <c>webhook_deliveries</c>'s bare <c>id</c>, so <c>OutboxPruneQuery</c>/
/// <c>WebhookDeliveryPruneQuery</c>'s "<c>WHERE id IN (SELECT id ...)</c>" shape has no single column to
/// name here. <c>ctid</c> - Postgres's own physical row locator, valid for exactly one statement's
/// duration - is the standard substitute for bounding a delete by any key shape, composite or not.
/// </summary>
public static class InboxPruneQuery
{
    public static async Task<int> DeleteOlderThanBatchAsync(
        NpgsqlConnection connection, DateTimeOffset olderThan, int batchSize, CancellationToken cancellationToken)
    {
        const string sql = """
            DELETE FROM inbox
            WHERE ctid IN (
                SELECT ctid
                FROM inbox
                WHERE processed_at < @olderThan
                ORDER BY processed_at
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
