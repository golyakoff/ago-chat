using Npgsql;

namespace Ago.Chat.Worker;

/// <summary>
/// `25-72`: the atomic-claim half of <see cref="SiteExportJob"/>'s own request queue, replacing
/// <see cref="SiteExportQuery.ListPendingAsync"/>'s plain <c>SELECT</c> - a read with no claim, which
/// let two <c>Ago.Chat.Worker</c> replicas ticking close together both read the same <c>Pending</c> row
/// and both build/upload the same tenant's archive (`25-72`'s own Found note).
///
/// <para><b>Same claim shape as <see cref="SiteExportPruneQuery.ClaimExpiredReadyBatchAsync"/>, its
/// freshly-built sibling in this same file's own directory</b> - a <c>WITH candidates AS (... FOR
/// UPDATE SKIP LOCKED)</c> claim, materialised before the <c>UPDATE</c> that flips status, the identical
/// single-statement "claim and resolve in one round trip" property that class's own remarks describe in
/// full. Deliberately <b>simpler</b> than that sibling, not a second pattern: this claim does not need
/// to hand its caller any pre-update column value (the prune claim needs the <c>object_key</c> as it
/// stood *before* the same statement nulls it, so it materialises the candidates in a first CTE and
/// reads that CTE's own pre-update column in its final <c>RETURNING</c>) - here the only two columns
/// the caller needs (<c>id</c>, <c>site_id</c>) are unaffected by the <c>UPDATE</c> itself, so a plain
/// <c>UPDATE ... WHERE id IN (SELECT ... FOR UPDATE SKIP LOCKED) RETURNING</c> already gives the caller
/// everything it needs from the row's own *post*-update state.</para>
///
/// <para><b>Why a status flip, not a lock held across the whole build-and-upload.</b>
/// <see cref="SiteExportJob.ProcessExportAsync"/> opens and closes several separate connections across
/// a potentially slow operation - the archive is streamed to a local temp file, then a separate HTTP
/// PUT uploads it - it does not (and, given that shape, realistically cannot) hold one open transaction
/// for the whole duration. Holding a <c>FOR UPDATE SKIP LOCKED</c> row lock (and the connection carrying
/// it) open across that entire slow operation would risk connection-pool exhaustion under real load for
/// no correctness gain a status flip does not already give. So the claim here is deliberately narrow: one
/// quick transaction claims a batch by flipping <c>status</c> to <see cref="Domain.ExportStatus.Processing"/>
/// and releases immediately; the slow build/upload work that follows holds no lock at all; a final quick
/// write (<see cref="SiteExportQuery.MarkReadyAsync"/>/<see cref="SiteExportQuery.MarkFailedAsync"/>,
/// both now scoped to <c>status = 'Processing'</c> rather than <c>'Pending'</c>) resolves the row.</para>
///
/// <para><b><see cref="ReclaimStaleBatchAsync"/>: the recovery half.</b> A replica that crashes between
/// claiming a batch and resolving it leaves a row stuck <c>Processing</c> forever unless something else
/// notices - this method is that something else, called once at the top of every
/// <see cref="SiteExportJob.SweepAsync"/> cycle (not a second timer/job): any row that has sat
/// <c>Processing</c> longer than <c>SiteExportJobOptions.StaleProcessingTimeout</c> is handed back to
/// <c>Pending</c>, plain and simple, so the very next claim in the same cycle can pick it up again. No
/// <c>FOR UPDATE SKIP LOCKED</c> needed here: unlike the claim query above, this statement's own
/// <c>WHERE</c> clause already names specific, already-abandoned rows rather than racing every other
/// replica for "the next N available" - if two replicas' reclaim sweeps somehow overlap on the same
/// row, the second one's <c>UPDATE</c> simply finds zero matching rows left (the first already flipped
/// it), which is a correct no-op, not a race.</para>
/// </summary>
public static class SiteExportClaimQuery
{
    public static async Task<IReadOnlyList<PendingExport>> ClaimPendingBatchAsync(
        NpgsqlConnection connection, DateTimeOffset now, int batchSize, CancellationToken cancellationToken)
    {
        const string sql = """
            UPDATE export_requests
            SET status = 'Processing', processing_started_at = @now
            WHERE id IN (
                SELECT id
                FROM export_requests
                WHERE status = 'Pending'
                ORDER BY requested_at
                LIMIT @batchSize
                FOR UPDATE SKIP LOCKED
            )
            RETURNING id, site_id
            """;

        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("now", now);
        command.Parameters.AddWithValue("batchSize", batchSize);

        var claimed = new List<PendingExport>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            claimed.Add(new PendingExport(reader.GetGuid(0), reader.GetGuid(1)));
        }

        return claimed;
    }

    /// <summary>Hands every <c>Processing</c> row older than <paramref name="olderThan"/> back to
    /// <c>Pending</c>, clearing its own <c>processing_started_at</c> so the row looks exactly like a
    /// request nothing has ever claimed - <see cref="ClaimPendingBatchAsync"/>'s own <c>ORDER BY
    /// requested_at</c> is what decides when it is claimed again, not this method. Returns the count
    /// reclaimed, purely for the caller's own log line - <see cref="SiteExportJob.SweepAsync"/>'s own
    /// <c>completed</c> count is unrelated and never includes this number.</summary>
    public static async Task<int> ReclaimStaleBatchAsync(
        NpgsqlConnection connection, DateTimeOffset olderThan, CancellationToken cancellationToken)
    {
        const string sql = """
            UPDATE export_requests
            SET status = 'Pending', processing_started_at = NULL
            WHERE status = 'Processing' AND processing_started_at < @olderThan
            """;

        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("olderThan", olderThan);
        return await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
