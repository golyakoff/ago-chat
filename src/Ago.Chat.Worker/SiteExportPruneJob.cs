using Ago.Chat.Application.Abstractions;
using Ago.Chat.Contracts;
using Ago.Platform.Abstractions;
using Ago.Platform.Kernel;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Ago.Chat.Worker;

/// <summary>
/// This item's own TTL half: `16-03` shipped an export archive with no expiry at all - the object sat
/// in MinIO/S3 forever, and the request row said so was "Forever. No pruning exists"
/// (`personal-data.md`'s own `export_requests` row, before this item). Same `PeriodicTimer`/
/// `BackgroundService`/bounded-batches-per-cycle shape <see cref="AccessRecordPruneJob"/> already
/// establishes for a scheduled prune; the per-row delete-then-resolve step instead follows
/// <see cref="AttachmentOrphanSweepJob"/>'s own shape, because this job - unlike
/// <see cref="AccessRecordPruneJob"/>'s plain audit-row delete - also has to reconcile an object-storage
/// delete against the database, exactly the problem that job already solved.
///
/// <para><b>Claim first, delete second - the row is resolved before the storage call, not after.</b>
/// <see cref="SiteExportPruneQuery.ClaimExpiredReadyBatchAsync"/> flips each claimed row to
/// <c>Expired</c> (and nulls its <c>ObjectKey</c>) in the same atomic statement that reads it, before
/// this method ever calls <see cref="IFileStorage.DeleteAsync"/>. If that delete then fails
/// (<see cref="FileStorageUnavailableException"/>, the one recognised transient failure
/// <see cref="AttachmentOrphanSweepJob"/>'s own remarks already accept for the identical reason), the
/// database row is not left stuck mid-resolution the way a "delete first, then update" ordering would
/// leave it on a crash between the two steps - it is already <c>Expired</c>, and the object may now be
/// an orphan the same way that job's own comment states plainly for its own rows. S3/MinIO's own
/// `DELETE` is idempotent (that job's own test: "no exception expected") - if this ever needs a real
/// reconciliation sweep for orphaned objects, it is the same kind of gap 25-72 already names for
/// export's own claim, not a new one.</para>
/// </summary>
public sealed class SiteExportPruneJob(
    NpgsqlDataSource dataSource,
    IFileStorage fileStorage,
    IClock clock,
    IOptions<SiteExportPruneJobOptions> options,
    ILogger<SiteExportPruneJob> logger) : BackgroundService
{
    private const string TableTag = "site_export_prune";

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
                logger.LogError(ex, "Site export prune cycle failed; retrying next cycle.");
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    /// <summary>One bounded pass, up to <see cref="SiteExportPruneJobOptions.MaxBatchesPerCycle"/>
    /// batches of <see cref="SiteExportPruneJobOptions.BatchSize"/> rows each - the same
    /// "cap the work one cycle can do" shape <see cref="AccessRecordPruneJob.PruneAsync"/> already
    /// establishes. <c>internal</c> for the identical reason every other job in this file exposes
    /// one: an integration test drives exactly one cycle instead of waiting for a timer.</summary>
    internal async Task<int> PruneAsync(CancellationToken cancellationToken)
    {
        var startedAt = clock.UtcNow;
        var olderThan = startedAt - options.Value.RetentionWindow;

        var totalExpired = 0;
        for (var batch = 0; batch < options.Value.MaxBatchesPerCycle; batch++)
        {
            IReadOnlyList<ClaimedExport> claimed;
            await using (var connection = await dataSource.OpenConnectionAsync(cancellationToken))
            {
                claimed = await SiteExportPruneQuery.ClaimExpiredReadyBatchAsync(
                    connection, olderThan, options.Value.BatchSize, cancellationToken);
            }

            if (claimed.Count == 0)
            {
                break;
            }

            foreach (var item in claimed)
            {
                // The row is already Expired by the time this runs (ClaimExpiredReadyBatchAsync's own
                // remarks) - a delete failure here never leaves it stuck, only possibly orphaned.
                if (item.ObjectKey is not { } objectKey)
                {
                    continue;
                }

                try
                {
                    await fileStorage.DeleteAsync(new ObjectKey(objectKey), cancellationToken);
                }
                catch (FileStorageUnavailableException ex)
                {
                    logger.LogWarning(
                        ex,
                        "Expired export request {ExportId} but could not delete its storage object " +
                        "{ObjectKey}; it may now be an orphan.",
                        item.ExportId, objectKey);
                }
            }

            totalExpired += claimed.Count;

            if (claimed.Count < options.Value.BatchSize)
            {
                break;
            }
        }

        if (totalExpired > 0)
        {
            logger.LogInformation("Site export prune expired {Count} export request(s).", totalExpired);
        }

        ChatMetrics.RecordRetentionPruneCycle(TableTag, totalExpired, clock.UtcNow - startedAt);
        return totalExpired;
    }
}
