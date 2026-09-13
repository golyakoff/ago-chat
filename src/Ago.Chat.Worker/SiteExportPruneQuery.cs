using Npgsql;

namespace Ago.Chat.Worker;

/// <summary>
/// This item's own TTL sweep query - one atomic claim-and-resolve statement, the same
/// "<c>UPDATE ... WHERE ... RETURNING</c> is the ordering guarantee" shape
/// <see cref="AttachmentOrphanSweepQuery.ClaimExpiredPendingBatchAsync"/>'s own remarks describe,
/// adapted from a <c>DELETE</c> to an <c>UPDATE</c> because this table's own row must survive (the
/// console's export-history screen still needs to show a past, now-expired request), not disappear the
/// way an abandoned attachment row does.
///
/// <para><b>Why a <c>WITH candidates AS (... FOR UPDATE SKIP LOCKED)</c> CTE, rather than the plain
/// subquery <c>ix_export_requests_pending</c>'s own claim (<c>SiteExportQuery.ListPendingAsync</c>)
/// uses.</b> This statement needs the row's <i>object key as it was before this same statement nulls
/// it</i> - the value <see cref="ClaimExpiredReadyBatchAsync"/>'s caller must hand to
/// <c>IFileStorage.DeleteAsync</c>. An <c>UPDATE ... RETURNING</c> alone can only return the row's
/// <i>post</i>-update columns, which would already be <see langword="null"/> for <c>object_key</c> by
/// the time <c>RETURNING</c> evaluates. Materialising the candidate rows first, then joining the
/// <c>UPDATE</c> against that materialisation and returning the CTE's own (pre-update) column, is the
/// standard way Postgres exposes an old value alongside a new one in a single statement - the same
/// single-round-trip, single-transaction property <see cref="AttachmentOrphanSweepQuery"/>'s own
/// remarks call "the ordering guarantee," here extended to "claim it and still get to see what it
/// looked like beforehand."</para>
///
/// <para><b>Claims are atomic per row (<c>FOR UPDATE SKIP LOCKED</c>), but resolving the storage
/// delete afterwards is not re-entered into the same transaction</b> - the identical, already-accepted
/// gap <c>25-72</c> names for <c>SiteExportJob</c>'s own claim (<c>SiteExportQuery.ListPendingAsync</c>'s
/// own remarks: no atomic cross-replica claim exists there either). Here the DB side is stronger than
/// that sibling - a row this statement claims is already flipped to <c>Expired</c> before the
/// <c>IFileStorage.DeleteAsync</c> call even starts (<see cref="SiteExportPruneJob"/>'s own remarks),
/// so two replicas can never both attempt to expire the same row - only "an object delete that fails
/// after the DB row already says Expired" remains a possible, tolerated outcome, the same one
/// <c>AttachmentOrphanSweepJob</c> already tolerates for its own rows.</para>
/// </summary>
public static class SiteExportPruneQuery
{
    public static async Task<IReadOnlyList<ClaimedExport>> ClaimExpiredReadyBatchAsync(
        NpgsqlConnection connection, DateTimeOffset olderThan, int batchSize, CancellationToken cancellationToken)
    {
        const string sql = """
            WITH candidates AS (
                SELECT id, object_key
                FROM export_requests
                WHERE status = 'Ready' AND completed_at < @olderThan
                ORDER BY completed_at
                LIMIT @batchSize
                FOR UPDATE SKIP LOCKED
            ),
            claimed AS (
                UPDATE export_requests e
                SET status = 'Expired', object_key = NULL
                FROM candidates c
                WHERE e.id = c.id
                RETURNING e.id, c.object_key
            )
            SELECT id, object_key FROM claimed
            """;

        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("olderThan", olderThan);
        command.Parameters.AddWithValue("batchSize", batchSize);

        var claimed = new List<ClaimedExport>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var id = reader.GetGuid(0);
            var objectKey = reader.IsDBNull(1) ? null : reader.GetString(1);
            claimed.Add(new ClaimedExport(id, objectKey));
        }

        return claimed;
    }
}

/// <summary>One <c>Ready</c> export request this cycle claimed and flipped to <c>Expired</c> - its own
/// pre-update <c>ObjectKey</c>, the value <see cref="SiteExportPruneJob"/> still needs to delete from
/// object storage. Can, in principle, be <see langword="null"/> if a row somehow reached <c>Ready</c>
/// without one (never produced by <see cref="Ago.Chat.Worker.SiteExportQuery.MarkReadyAsync"/> today,
/// but the caller checks rather than assumes).</summary>
public readonly record struct ClaimedExport(Guid ExportId, string? ObjectKey);

