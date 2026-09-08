using Ago.Chat.Application.Abstractions;
using Ago.Chat.Domain;
using Npgsql;

namespace Ago.Chat.Infrastructure.Postgres;

/// <summary>
/// `23-68`: raw Npgsql, not EF - <see cref="IOperatorSeatRestoreOverrideRepository"/>'s own remarks
/// explain why (no aggregate, no invariant beyond "one row per exercised override", the same reasoning
/// <see cref="ModuleRevokeOverrideRepository"/>/<see cref="AccessRecordRepository"/> already give for
/// themselves).
/// </summary>
public sealed class OperatorSeatRestoreOverrideRepository(NpgsqlDataSource dataSource) : IOperatorSeatRestoreOverrideRepository
{
    public async Task RecordAsync(
        Guid id, SiteId siteId, OperatorId operatorId, string restoredBy, string reason, DateTimeOffset restoredAt,
        CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
            """
            insert into operator_seat_restore_overrides (id, site_id, operator_id, restored_by, reason, restored_at)
            values (@id, @siteId, @operatorId, @restoredBy, @reason, @restoredAt)
            """,
            connection);
        command.Parameters.AddWithValue("id", id);
        command.Parameters.AddWithValue("siteId", siteId.Value);
        command.Parameters.AddWithValue("operatorId", operatorId.Value);
        command.Parameters.AddWithValue("restoredBy", restoredBy);
        command.Parameters.AddWithValue("reason", reason);
        command.Parameters.AddWithValue("restoredAt", restoredAt);

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<OperatorSeatRestoreOverrideRecord>> ListForSiteAsync(
        SiteId siteId, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
            """
            select id, operator_id, restored_by, reason, restored_at
            from operator_seat_restore_overrides
            where site_id = @siteId
            order by restored_at
            """,
            connection);
        command.Parameters.AddWithValue("siteId", siteId.Value);

        var items = new List<OperatorSeatRestoreOverrideRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            items.Add(new OperatorSeatRestoreOverrideRecord(
                reader.GetGuid(0), siteId, new OperatorId(reader.GetGuid(1)), reader.GetString(2),
                reader.GetString(3), reader.GetFieldValue<DateTimeOffset>(4)));
        }

        return items;
    }
}
