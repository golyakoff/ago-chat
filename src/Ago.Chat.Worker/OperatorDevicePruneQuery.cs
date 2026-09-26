using Npgsql;

namespace Ago.Chat.Worker;

/// <summary>
/// `26-123`/`adr/0185`: the bounded-batch revoke half of the stale-device prune -
/// <see cref="OutboxPruneQuery"/>/<see cref="AccessRecordPruneQuery"/>'s own raw-Npgsql-in-the-Worker
/// shape, an `UPDATE ... WHERE id IN (SELECT ... LIMIT ... FOR UPDATE SKIP LOCKED)` rather than a bare
/// `UPDATE ... LIMIT` for the identical reason those two take it: Postgres's `UPDATE` has no `LIMIT`
/// clause of its own, and `FOR UPDATE SKIP LOCKED` means this job never blocks on - or steals - a row
/// <see cref="Ago.Chat.Application.UseCases.NotifyOperatorDevices.NotifyOperatorDevicesHandler"/> or
/// <see cref="OperatorDeviceRevoker"/> is concurrently writing (a device that gets a fresh push outcome
/// or an operator-removal revoke in the same instant this job's batch is selecting is simply left for
/// the next cycle, not raced).
///
/// <para><b>An `UPDATE`, never a `DELETE`</b> - the same "revoke, don't erase" choice every other
/// revocation cause on this table already makes (`OperatorDevice.Revoke`'s own idempotent mutation),
/// kept here rather than switched to a delete for the identical reason: a pruned row stays exactly as
/// auditable as a signed-out one, and a device that comes back online re-registers through the normal
/// upsert path (`RegisterOperatorDeviceHandler`), reviving the same row.</para>
///
/// <para><b>Idempotent by construction</b>, per rule 5 - the inner `WHERE revoked_at IS NULL` means a
/// row this job already revoked is invisible to a later run (or a concurrent replica's own batch) even
/// before `FOR UPDATE SKIP LOCKED` is considered; a redelivered or duplicated cycle revokes nothing a
/// second time.</para>
/// </summary>
public static class OperatorDevicePruneQuery
{
    public static async Task<int> RevokeStaleBatchAsync(
        NpgsqlConnection connection, DateTimeOffset now, DateTimeOffset cutoff, int batchSize,
        CancellationToken cancellationToken)
    {
        const string sql = """
            UPDATE operator_devices
            SET revoked_at = @now
            WHERE id IN (
                SELECT id
                FROM operator_devices
                WHERE revoked_at IS NULL AND last_seen_at < @cutoff
                ORDER BY last_seen_at
                LIMIT @batchSize
                FOR UPDATE SKIP LOCKED
            )
            """;

        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("now", now);
        command.Parameters.AddWithValue("cutoff", cutoff);
        command.Parameters.AddWithValue("batchSize", batchSize);

        return await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
