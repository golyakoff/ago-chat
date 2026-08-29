using Ago.Chat.Application.Abstractions;
using Ago.Chat.Contracts;
using Ago.Platform.Abstractions;
using Ago.Platform.Kernel;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Ago.Chat.Worker;

/// <summary>
/// `15-04`/`adr/0031`: the drop half of `data-model.md`'s partitioning story - `2-06`'s own out-of-scope
/// note named `DROP` as the cheap-retention mechanism partitioning enables and deferred building it
/// here. Same shape as <see cref="PartitionMaintenanceJob"/> (its own natural counterpart - one creates
/// ahead of need, this one drops past need) but gated where that one is not: every drop candidate is
/// checked against <see cref="IMessageArchiveGate"/> first, per `adr/0031`'s ordering rule ("nothing is
/// dropped until its archive is confirmed written"). `13-06` replaces `15-04`'s
/// <c>AlwaysConfirmedMessageArchiveGate</c> stand-in with <see cref="MessageArchiveGate"/>, a real,
/// object-storage-backed implementation - only the DI registration in <c>ChatModule</c> changed for
/// that; this job's own gate-check code is untouched, exactly as `15-04` anticipated.
///
/// <para><b>`13-06`: attachment expiry is a direct consequence of a successful drop, not a separate
/// cutoff computation.</b> Immediately before dropping a confirmed-archived partition, this job reads
/// the exact <c>attachment_id</c>s that partition's own rows reference
/// (<see cref="MessagePartitionPruneQuery.ListReferencedAttachmentIdsAsync"/> - see its own remarks for
/// why a date-range query against the separate `attachments` table cannot substitute for this), then
/// after the drop deletes exactly those <c>attachments</c> rows and their storage objects
/// (<see cref="AttachmentRetentionSweepQuery"/>, reusing `5-04`'s own delete-then-clean-up-storage
/// shape). "Attachments follow their message's window" (`adr/0031`'s Decision 4) is therefore true by
/// construction: an attachment can only be swept in the same call that drops the one partition whose
/// rows referenced it.</para>
/// </summary>
public sealed class MessagePartitionPruneJob(
    NpgsqlDataSource dataSource,
    IMessageArchiveGate archiveGate,
    IFileStorage fileStorage,
    IClock clock,
    IOptions<MessagePartitionPruneJobOptions> options,
    ILogger<MessagePartitionPruneJob> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(options.Value.Interval);
        do
        {
            try
            {
                await PruneAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Message partition prune cycle failed; retrying next cycle.");
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    internal async Task PruneAsync(CancellationToken cancellationToken)
    {
        var startedAt = clock.UtcNow;
        var currentMonthStart = new DateOnly(startedAt.Year, startedAt.Month, 1);
        var cutoff = currentMonthStart.AddMonths(-options.Value.RetentionHorizonMonths);

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        var partitions = await MessagePartitionPruneQuery.ListPartitionsAsync(connection, cancellationToken);

        var dropped = 0;
        var pendingArchive = 0;
        foreach (var partition in partitions)
        {
            if (partition.PeriodEnd > cutoff)
            {
                // Not past the horizon yet - includes, by construction, every partition
                // PartitionMaintenanceJob is actively keeping ready (RetentionHorizonMonths is
                // validated >= 1, so the current month is never a candidate).
                continue;
            }

            var confirmed = await archiveGate.IsArchivedAsync(
                partition.Name, partition.PeriodStart, partition.PeriodEnd, cancellationToken);
            if (!confirmed)
            {
                logger.LogInformation(
                    "Messages partition {Partition} is past its retention horizon but not yet archive-confirmed; leaving it in place.",
                    partition.Name);
                pendingArchive++;
                continue;
            }

            // Read before the drop, deliberately - once the partition is gone there is no way left to
            // ask which attachments its rows referenced (this class's own remarks explain why a
            // date-range query against the separate `attachments` table cannot substitute).
            var attachmentIds = await MessagePartitionPruneQuery.ListReferencedAttachmentIdsAsync(
                connection, partition.Name, cancellationToken);

            await MessagePartitionPruneQuery.DropPartitionAsync(connection, partition.Name, cancellationToken);
            logger.LogInformation("Dropped messages partition {Partition} (past its {Months}-month retention horizon).",
                partition.Name, options.Value.RetentionHorizonMonths);
            dropped++;

            await SweepAttachmentsAsync(connection, attachmentIds, partition.Name, cancellationToken);
        }

        ChatMetrics.RecordPartitionPruneCycle(dropped, pendingArchive, clock.UtcNow - startedAt);
    }

    /// <summary>`13-06`: deletes exactly the `attachments` rows a just-dropped partition's own rows
    /// referenced, then their storage objects - `AttachmentOrphanSweepJob`'s own established split
    /// between "the row is gone, that is the durable fact" and "best-effort clean-up of the object
    /// that now has no row pointing at it," restated here for a different predicate. A storage delete
    /// failure is logged and does not roll anything back: the attachment row (and, with it, the
    /// tenant's own record that this data ever existed) is already gone by design - `5-02`'s own "S3
    /// DELETE is idempotent" property means a later retry of the same key is harmless if the object
    /// genuinely is still there, and the object provider's own lifecycle rules are the backstop this
    /// codebase has never promised to duplicate for an orphan that outlives its row.</summary>
    private async Task SweepAttachmentsAsync(
        NpgsqlConnection connection, IReadOnlyList<Guid> attachmentIds, string partitionName, CancellationToken cancellationToken)
    {
        if (attachmentIds.Count == 0)
        {
            return;
        }

        var deleted = await AttachmentRetentionSweepQuery.DeleteByIdsAsync(connection, attachmentIds, cancellationToken);
        foreach (var attachment in deleted)
        {
            try
            {
                await fileStorage.DeleteAsync(new ObjectKey(attachment.ObjectKey), cancellationToken);
                if (attachment.ThumbnailKey is { } thumbnailKey)
                {
                    await fileStorage.DeleteAsync(new ObjectKey(thumbnailKey), cancellationToken);
                }
            }
            catch (FileStorageUnavailableException ex)
            {
                logger.LogWarning(
                    ex,
                    "Deleted attachment row {AttachmentId} (partition {Partition}) but could not delete its storage object(s); it may now be an orphan.",
                    attachment.Id, partitionName);
            }
        }

        if (deleted.Count > 0)
        {
            logger.LogInformation(
                "Retention sweep removed {Count} attachment(s) belonging to dropped partition {Partition}.",
                deleted.Count, partitionName);
        }
    }
}
