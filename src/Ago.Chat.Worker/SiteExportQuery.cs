using Npgsql;

namespace Ago.Chat.Worker;

/// <summary>
/// `16-03`: the request-queue half of <see cref="SiteExportJob"/> - raw Npgsql, the same shape
/// <see cref="SiteErasureQuery"/> already establishes for claiming and resolving a queue-shaped table.
/// The per-store content reads that build the archive itself live in
/// <see cref="SiteExportArchiveWriter"/>, kept separate from this file because those are a different
/// concern - "what does one site's personal data look like" rather than "which requests are
/// outstanding."
/// </summary>
public static class SiteExportQuery
{
    public static async Task<IReadOnlyList<PendingExport>> ListPendingAsync(
        NpgsqlConnection connection, int limit, CancellationToken cancellationToken)
    {
        const string sql = """
            select id, site_id
            from export_requests
            where status = 'Pending'
            order by requested_at
            limit @limit
            """;

        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("limit", limit);

        var pending = new List<PendingExport>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            pending.Add(new PendingExport(reader.GetGuid(0), reader.GetGuid(1)));
        }

        return pending;
    }

    /// <summary>Terminal success: records the finished archive's object key and completion time.
    /// Scoped to <c>status = 'Pending'</c> so a request already resolved by a previous, crashed attempt
    /// at this same row (see <see cref="SiteExportJob.ProcessExportAsync"/>'s own remarks on why that
    /// window is only theoretical today) can never be overwritten by a stale second writer.</summary>
    public static async Task<int> MarkReadyAsync(
        NpgsqlConnection connection, Guid exportId, string objectKey, DateTimeOffset completedAt, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            update export_requests
            set status = 'Ready', object_key = @objectKey, completed_at = @completedAt
            where id = @id and status = 'Pending'
            """,
            connection);
        command.Parameters.AddWithValue("id", exportId);
        command.Parameters.AddWithValue("objectKey", objectKey);
        command.Parameters.AddWithValue("completedAt", completedAt);
        return await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>Terminal failure: <see cref="Domain.ExportStatus.Failed"/> is not retried automatically
    /// (<see cref="Domain.ExportStatus"/>'s own remarks) - the tenant can simply ask again.</summary>
    public static async Task<int> MarkFailedAsync(
        NpgsqlConnection connection, Guid exportId, string failureReason, DateTimeOffset completedAt, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            update export_requests
            set status = 'Failed', failure_reason = @failureReason, completed_at = @completedAt
            where id = @id and status = 'Pending'
            """,
            connection);
        command.Parameters.AddWithValue("id", exportId);
        command.Parameters.AddWithValue("failureReason", failureReason);
        command.Parameters.AddWithValue("completedAt", completedAt);
        return await command.ExecuteNonQueryAsync(cancellationToken);
    }
}

public readonly record struct PendingExport(Guid ExportId, Guid SiteId);
