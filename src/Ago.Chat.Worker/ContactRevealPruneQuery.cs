using Npgsql;

namespace Ago.Chat.Worker;

/// <summary>`23-11`: <see cref="AccessRecordPruneQuery"/>'s own shape, applied to
/// <c>contact_reveals</c> and keyed by <c>occurred_at</c> - the same column name
/// <see cref="Ago.Chat.Infrastructure.Postgres.Persistence.ContactRevealEntity"/> already uses for
/// when the reveal happened. <c>FOR UPDATE SKIP LOCKED</c> costs nothing and keeps this query's shape
/// identical to its siblings, even though nothing else ever updates a reveal record after it is
/// written - a write-once row, the same as <c>access_records</c>/<c>webhook_deliveries</c>.</summary>
public static class ContactRevealPruneQuery
{
    public static async Task<int> DeleteOlderThanBatchAsync(
        NpgsqlConnection connection, DateTimeOffset olderThan, int batchSize, CancellationToken cancellationToken)
    {
        const string sql = """
            DELETE FROM contact_reveals
            WHERE id IN (
                SELECT id
                FROM contact_reveals
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
