using Ago.Chat.Application.Abstractions;
using Ago.Chat.Domain;
using Npgsql;

namespace Ago.Chat.Infrastructure.Postgres;

/// <summary>
/// `16-03`: raw Npgsql, not EF - <see cref="IExportRequestRepository"/>'s own remarks explain why.
/// </summary>
public sealed class ExportRequestRepository(NpgsqlDataSource dataSource) : IExportRequestRepository
{
    public async Task<bool> CreateAsync(
        Guid exportId, SiteId siteId, OperatorId requestedBy, DateTimeOffset requestedAt, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        // `where exists (...)`, not a separate existence check first: one round trip, and a site
        // deleted between the check and the insert can never produce an orphaned export_requests row
        // (the check and the write happen atomically inside Postgres's own statement execution, not
        // across two round trips this application could interleave a delete into).
        await using var command = new NpgsqlCommand(
            """
            insert into export_requests (id, site_id, requested_by, status, requested_at)
            select @id, @siteId, @requestedBy, 'Pending', @requestedAt
            where exists (select 1 from sites where id = @siteId)
            """,
            connection);
        command.Parameters.AddWithValue("id", exportId);
        command.Parameters.AddWithValue("siteId", siteId.Value);
        command.Parameters.AddWithValue("requestedBy", requestedBy.Value);
        command.Parameters.AddWithValue("requestedAt", requestedAt);

        var affected = await command.ExecuteNonQueryAsync(cancellationToken);
        return affected > 0;
    }

    public async Task<ExportRequestRecord?> GetAsync(Guid exportId, SiteId siteId, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
            """
            select status, object_key, failure_reason, requested_at, completed_at
            from export_requests
            where id = @id and site_id = @siteId
            """,
            connection);
        command.Parameters.AddWithValue("id", exportId);
        command.Parameters.AddWithValue("siteId", siteId.Value);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        var status = Enum.Parse<ExportStatus>(reader.GetString(0));
        var objectKey = reader.IsDBNull(1) ? null : reader.GetString(1);
        var failureReason = reader.IsDBNull(2) ? null : reader.GetString(2);
        var requestedAt = reader.GetFieldValue<DateTimeOffset>(3);
        DateTimeOffset? completedAt = reader.IsDBNull(4) ? null : reader.GetFieldValue<DateTimeOffset>(4);

        return new ExportRequestRecord(exportId, status, objectKey, failureReason, requestedAt, completedAt);
    }
}
