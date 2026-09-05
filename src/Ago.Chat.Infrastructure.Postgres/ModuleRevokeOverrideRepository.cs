using Ago.Chat.Application.Abstractions;
using Ago.Chat.Domain;
using Npgsql;

namespace Ago.Chat.Infrastructure.Postgres;

/// <summary>
/// `23-13`: raw Npgsql, not EF - <see cref="IModuleRevokeOverrideRepository"/>'s own remarks explain
/// why (no aggregate, no invariant beyond "one row per exercised override", the same reasoning
/// <see cref="ExportRequestRepository"/>/<see cref="AccessRecordRepository"/> already give for
/// themselves).
/// </summary>
public sealed class ModuleRevokeOverrideRepository(NpgsqlDataSource dataSource) : IModuleRevokeOverrideRepository
{
    public async Task RecordAsync(
        Guid id, SiteId siteId, string moduleKey, string revokedBy, string reason, DateTimeOffset revokedAt,
        CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
            """
            insert into module_revoke_overrides (id, site_id, module_key, revoked_by, reason, revoked_at)
            values (@id, @siteId, @moduleKey, @revokedBy, @reason, @revokedAt)
            """,
            connection);
        command.Parameters.AddWithValue("id", id);
        command.Parameters.AddWithValue("siteId", siteId.Value);
        command.Parameters.AddWithValue("moduleKey", moduleKey);
        command.Parameters.AddWithValue("revokedBy", revokedBy);
        command.Parameters.AddWithValue("reason", reason);
        command.Parameters.AddWithValue("revokedAt", revokedAt);

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<ModuleRevokeOverrideRecord>> ListForSiteAsync(
        SiteId siteId, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
            """
            select id, module_key, revoked_by, reason, revoked_at
            from module_revoke_overrides
            where site_id = @siteId
            order by revoked_at
            """,
            connection);
        command.Parameters.AddWithValue("siteId", siteId.Value);

        var items = new List<ModuleRevokeOverrideRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            items.Add(new ModuleRevokeOverrideRecord(
                reader.GetGuid(0), siteId, reader.GetString(1), reader.GetString(2), reader.GetString(3),
                reader.GetFieldValue<DateTimeOffset>(4)));
        }

        return items;
    }
}
