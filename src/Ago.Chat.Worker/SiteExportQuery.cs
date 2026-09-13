using Npgsql;

namespace Ago.Chat.Worker;

/// <summary>
/// `16-03`: the request-queue half of <see cref="SiteExportJob"/> - raw Npgsql, the same shape
/// <see cref="SiteErasureQuery"/> already establishes for claiming and resolving a queue-shaped table.
/// The per-store content reads that build the archive itself live in
/// <see cref="SiteExportArchiveWriter"/>, kept separate from this file because those are a different
/// concern - "what does one site's personal data look like" rather than "which requests are
/// outstanding." `25-72`: the read that used to live here, <c>ListPendingAsync</c> (a plain
/// <c>SELECT ... WHERE status = 'Pending'</c>, no claim), moved to
/// <see cref="SiteExportClaimQuery.ClaimPendingBatchAsync"/> - that file's own remarks explain why a
/// read with no atomic claim let two replicas process the same request.
/// </summary>
public static class SiteExportQuery
{
    /// <summary>Terminal success: records the finished archive's object key and completion time.
    /// Scoped to <c>status = 'Processing'</c> (`25-72`; was <c>'Pending'</c> before
    /// <see cref="SiteExportClaimQuery.ClaimPendingBatchAsync"/> existed to put a row there) so a
    /// request already resolved by a previous, crashed attempt at this same row (see
    /// <see cref="SiteExportJob.ProcessExportAsync"/>'s own remarks on why that window is only
    /// theoretical today) can never be overwritten by a stale second writer - and, just as important,
    /// so this write actually matches the row it means to resolve: by the time a request reaches this
    /// call it has already been claimed into <c>Processing</c>, and a <c>WHERE status = 'Pending'</c>
    /// left unchanged here would silently match zero rows forever.</summary>
    public static async Task<int> MarkReadyAsync(
        NpgsqlConnection connection, Guid exportId, string objectKey, DateTimeOffset completedAt, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            update export_requests
            set status = 'Ready', object_key = @objectKey, completed_at = @completedAt, processing_started_at = NULL
            where id = @id and status = 'Processing'
            """,
            connection);
        command.Parameters.AddWithValue("id", exportId);
        command.Parameters.AddWithValue("objectKey", objectKey);
        command.Parameters.AddWithValue("completedAt", completedAt);
        return await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>Terminal failure: <see cref="Domain.ExportStatus.Failed"/> is not retried automatically
    /// (<see cref="Domain.ExportStatus"/>'s own remarks) - the tenant can simply ask again. Scoped to
    /// <c>status = 'Processing'</c>, the identical `25-72` reasoning <see cref="MarkReadyAsync"/>'s own
    /// remarks give.</summary>
    public static async Task<int> MarkFailedAsync(
        NpgsqlConnection connection, Guid exportId, string failureReason, DateTimeOffset completedAt, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            update export_requests
            set status = 'Failed', failure_reason = @failureReason, completed_at = @completedAt, processing_started_at = NULL
            where id = @id and status = 'Processing'
            """,
            connection);
        command.Parameters.AddWithValue("id", exportId);
        command.Parameters.AddWithValue("failureReason", failureReason);
        command.Parameters.AddWithValue("completedAt", completedAt);
        return await command.ExecuteNonQueryAsync(cancellationToken);
    }
}

public readonly record struct PendingExport(Guid ExportId, Guid SiteId);
