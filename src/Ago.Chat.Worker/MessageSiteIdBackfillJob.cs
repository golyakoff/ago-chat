using Microsoft.Extensions.Options;
using Npgsql;

namespace Ago.Chat.Worker;

/// <summary>
/// `18-01`: fills in `messages.site_id` for every row written before the column existed, resolving it
/// through the owning `conversations` row - the same one-time approximation shape `13-06`'s own scope
/// note names for `retention_class` ("nothing records what tier a historical message was written
/// under"), except here the true answer is not lost: `conversation_id -> conversations.site_id` is an
/// exact join, not a guess, so this backfill is not an approximation at all, only a delay.
///
/// <para><b>Bounded batches, per partition, not one `UPDATE` across the whole table.</b> `messages` is
/// this codebase's largest table by design (`2-06` partitioned it specifically so operations like this
/// one could be bounded); a single unbounded `UPDATE ... WHERE site_id IS NULL` would hold a lock
/// across however many million rows still need it, for however long that takes, which is exactly the
/// "expensive path partitioning exists to avoid" `adr/0031`'s own Context section names for a
/// different operation. Working one partition at a time, one bounded batch at a time, keeps every
/// individual statement short - each is its own implicit, autocommit transaction (no explicit `BEGIN`
/// on this connection), so a mid-run crash or restart loses at most one in-flight batch, not the whole
/// backfill; the next cycle's `WHERE site_id IS NULL` picks up exactly where the last one left off,
/// including under two Worker replicas racing the same partition (each batch is `UPDATE`, not
/// `UPDATE ... RETURNING` claimed exclusively first, so both can legitimately update overlapping rows -
/// setting the same correct `site_id` twice is idempotent, never wrong, only occasionally redundant
/// work).</para>
///
/// <para><b>Column stays nullable; there is no follow-up `NOT NULL` migration.</b> The column reads
/// `NULL` until this job's next cycle reaches a given row, and a search executed in that window simply
/// does not see it yet (`ConversationSearchStore`'s `site_id = @SiteId` predicate never matches a
/// `NULL`) - visible-but-incomplete, never wrong. Enforcing `NOT NULL` would require knowing the
/// backfill has fully converged, which is an operational fact this migration-time code cannot
/// observe; `Message.SiteId`'s own remarks record the nullable column as the honest permanent shape,
/// matching `AttachmentId`/`ClientMessageId` on the same entity.</para>
/// </summary>
public sealed class MessageSiteIdBackfillJob(
    NpgsqlDataSource dataSource,
    IOptions<MessageSiteIdBackfillJobOptions> options,
    ILogger<MessageSiteIdBackfillJob> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(options.Value.Interval);
        do
        {
            try
            {
                await BackfillAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Message site_id backfill cycle failed; retrying next cycle.");
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    internal async Task BackfillAsync(CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        var partitions = await MessagePartitionPruneQuery.ListPartitionsAsync(connection, cancellationToken);

        var totalBackfilled = 0;
        foreach (var partition in partitions)
        {
            int backfilledInPartition;
            do
            {
                backfilledInPartition = await BackfillBatchAsync(
                    connection, partition.Name, options.Value.BatchSize, cancellationToken);
                totalBackfilled += backfilledInPartition;
            } while (backfilledInPartition == options.Value.BatchSize);
        }

        if (totalBackfilled > 0)
        {
            logger.LogInformation(
                "Message site_id backfill updated {Count} row(s) across {PartitionCount} partition(s).",
                totalBackfilled, partitions.Count);
        }
    }

    /// <summary>One bounded batch, one partition. The `WHERE m.site_id IS NULL` on the outer `UPDATE`
    /// is technically redundant with the subquery's own filter (every `id` the subquery returns
    /// already has a null `site_id`) - kept anyway as an explicit invariant on the statement that
    /// actually writes, not just the one that selects, so the intent survives a future edit to either
    /// half in isolation.</summary>
    private static async Task<int> BackfillBatchAsync(
        NpgsqlConnection connection, string partitionName, int batchSize, CancellationToken cancellationToken)
    {
        var sql = $"""
            UPDATE {partitionName} m
            SET site_id = c.site_id
            FROM conversations c
            WHERE m.conversation_id = c.id
              AND m.site_id IS NULL
              AND m.id IN (SELECT id FROM {partitionName} WHERE site_id IS NULL LIMIT @batchSize)
            """;

        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("batchSize", batchSize);
        return await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
