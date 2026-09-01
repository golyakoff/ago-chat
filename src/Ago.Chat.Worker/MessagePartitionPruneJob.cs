using Ago.Chat.Application.Abstractions;
using Ago.Chat.Contracts;
using Ago.Chat.Domain;
using Ago.Platform.Abstractions;
using Ago.Platform.Kernel;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Ago.Chat.Worker;

/// <summary>
/// `15-04`/`adr/0031`: the removal half of `data-model.md`'s retention story - `2-06`'s own out-of-scope
/// note named this as the mechanism partitioning enables. Reworked for `15-09`/`adr/0087`: `messages` no
/// longer has one physical partition per (retention class, month), so removal is a `DELETE ... WHERE`
/// sweep over a discovered slice of rows rather than a `DROP TABLE` of a whole partition -
/// <b>`adr/0031`'s policy is unchanged</b> (retention class immutable, archive before removal, nothing
/// removed until confirmed archived) and only this mechanism changed.
///
/// <para>Same shape as <see cref="PartitionMaintenanceJob"/> used to be (this job's own natural
/// counterpart before that one was deleted - `adr/0087`: with no time axis there is nothing left to
/// maintain ahead of need, only behind it) but gated where a bare `DROP` never was: every removal
/// candidate is checked against <see cref="IMessageArchiveGate"/> first, per `adr/0031`'s ordering rule
/// ("nothing is removed until its archive is confirmed written").</para>
///
/// <para><b>Attachment expiry is still a direct consequence of a successful removal, not a separate
/// cutoff computation</b> (`13-06`'s own decision, restated for the new mechanism). Immediately before
/// removing a confirmed-archived slice's rows, this job reads the exact `attachment_id`s that slice's own
/// rows reference (<see cref="MessagePartitionPruneQuery.ListReferencedAttachmentIdsAsync"/>), then after
/// the removal deletes exactly those `attachments` rows and their storage objects
/// (<see cref="AttachmentRetentionSweepQuery"/>, `5-04`'s own delete-then-clean-up-storage shape).
/// "Attachments follow their message's window" (`adr/0031`'s Decision 4) is therefore still true by
/// construction: an attachment can only be swept in the same call that removes the one slice whose rows
/// referenced it.</para>
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

        var removedSlices = 0;
        var pendingArchive = 0;
        foreach (var bucketName in MessagePartitionNames.AllBucketNames)
        {
            var slices = await MessagePartitionPruneQuery.ListExpiredSlicesAsync(connection, bucketName, cutoff, cancellationToken);
            foreach (var slice in slices)
            {
                var confirmed = await archiveGate.IsArchivedAsync(
                    new SiteId(slice.SiteId), slice.RetentionClass, slice.PeriodStart, cancellationToken);
                if (!confirmed)
                {
                    logger.LogInformation(
                        "Site {SiteId}'s {RetentionClass} messages for {PeriodStart} are past their retention horizon but not yet archive-confirmed; leaving them in place.",
                        slice.SiteId, slice.RetentionClass, slice.PeriodStart);
                    pendingArchive++;
                    continue;
                }

                // Read before the delete, deliberately - once the rows are gone there is no way left
                // to ask which attachments they referenced (MessagePartitionPruneQuery's own remarks
                // explain why a date-range query against the separate `attachments` table cannot
                // substitute).
                var attachmentIds = await MessagePartitionPruneQuery.ListReferencedAttachmentIdsAsync(connection, slice, cancellationToken);

                var removedRows = await DeleteSliceAsync(connection, slice, cancellationToken);
                logger.LogInformation(
                    "Removed {RowCount} message(s) for site {SiteId}, class {RetentionClass}, period {PeriodStart} (past its {Months}-month retention horizon).",
                    removedRows, slice.SiteId, slice.RetentionClass, slice.PeriodStart, options.Value.RetentionHorizonMonths);
                removedSlices++;

                await SweepAttachmentsAsync(connection, attachmentIds, slice, cancellationToken);
            }
        }

        ChatMetrics.RecordPartitionPruneCycle(removedSlices, pendingArchive, clock.UtcNow - startedAt);
    }

    /// <summary>Drains one slice completely, bounded batch by bounded batch - the same "loop until a
    /// call returns fewer than requested" shape `MessageSiteIdBackfillJob`'s own per-partition loop
    /// already used before it was deleted.</summary>
    private async Task<int> DeleteSliceAsync(NpgsqlConnection connection, ExpiredMessageSlice slice, CancellationToken cancellationToken)
    {
        var total = 0;
        int removed;
        do
        {
            removed = await MessagePartitionPruneQuery.DeleteMessageBatchAsync(
                connection, slice, options.Value.DeleteBatchSize, cancellationToken);
            total += removed;
        } while (removed == options.Value.DeleteBatchSize);

        return total;
    }

    /// <summary>`13-06`: deletes exactly the `attachments` rows a just-removed slice's own rows
    /// referenced, then their storage objects - `AttachmentOrphanSweepJob`'s own established split
    /// between "the row is gone, that is the durable fact" and "best-effort clean-up of the object that
    /// now has no row pointing at it," restated here for a different predicate. A storage delete failure
    /// is logged and does not roll anything back: the attachment row (and, with it, the tenant's own
    /// record that this data ever existed) is already gone by design.</summary>
    private async Task SweepAttachmentsAsync(
        NpgsqlConnection connection, IReadOnlyList<Guid> attachmentIds, ExpiredMessageSlice slice, CancellationToken cancellationToken)
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
                    "Deleted attachment row {AttachmentId} (site {SiteId}, class {RetentionClass}, period {PeriodStart}) but could not delete its storage object(s); it may now be an orphan.",
                    attachment.Id, slice.SiteId, slice.RetentionClass, slice.PeriodStart);
            }
        }

        if (deleted.Count > 0)
        {
            logger.LogInformation(
                "Retention sweep removed {Count} attachment(s) belonging to site {SiteId}'s {RetentionClass} messages for period {PeriodStart}.",
                deleted.Count, slice.SiteId, slice.RetentionClass, slice.PeriodStart);
        }
    }
}
