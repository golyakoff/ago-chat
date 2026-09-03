using System.IO.Compression;
using System.Net.Http.Headers;
using Ago.Chat.Application.Abstractions;
using Ago.Chat.Domain;
using Ago.Platform.Abstractions;
using Ago.Platform.Kernel;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Ago.Chat.Worker;

/// <summary>
/// `13-06`/`adr/0031`, reworked for `15-09`/`adr/0087`: writes the archive object
/// <see cref="MessageArchiveGate"/> looks for and <see cref="MessagePartitionPruneJob"/> waits on before
/// it will ever remove a slice's rows - the "nothing is removed until its archive is confirmed written"
/// half of the ordering this item exists to preserve, unchanged in policy. Runs independently of, and
/// ahead of, that job: reads the identical discovery (<see cref="MessagePartitionPruneQuery.ListExpiredSlicesAsync"/>)
/// against the identical per-class horizon (`13-08`: shares
/// <see cref="MessagePartitionPruneJobOptions.EffectiveHorizonMonths"/> rather than a second,
/// independently-configurable number, or map, that could silently drift from the one the prune job
/// actually uses), so a slice never becomes a removal candidate before this job has had at
/// least one full cycle to notice it.
///
/// <para><b>One object per site per period</b> (`adr/0031`'s own wording), unchanged - only how a
/// candidate slice is discovered changed. Before `15-09`, one partition held every tenant's rows for a
/// (class, period), so this job enumerated a partition's own distinct `site_id`s; now
/// <see cref="MessagePartitionPruneQuery.ListExpiredSlicesAsync"/> already yields `(site_id,
/// retention_class, period)` tuples directly (it groups by all three), so there is no partition-level
/// enumeration step left to have. The `(retentionClass, periodStart)` -> already-archived-site-ids
/// lookup is cached per cycle (<see cref="IMessageArchiveRepository.ListArchivedSiteIdsAsync"/>) so a
/// (class, period) pair shared by several sites' slices (the common case) is looked up once, not once per
/// slice.</para>
///
/// <para><b>Never touches an unattributed row.</b> `site_id` is `NOT NULL` on every row as of `15-09`'s
/// own repartitioning migration (`Message.SiteId`'s own remarks), so the "any row with NULL site_id is
/// never confirmed" backpressure `MessageArchiveGate` used to need no longer applies - every slice this
/// job discovers already has a real, attributed site.</para>
/// </summary>
public sealed class MessageArchiveJob(
    NpgsqlDataSource dataSource,
    IFileStorage fileStorage,
    IMessageArchiveRepository archives,
    MessageArchiveWriter archiveWriter,
    IClock clock,
    IIdGenerator idGenerator,
    IOptions<MessagePartitionPruneJobOptions> pruneOptions,
    IOptions<MessageArchiveJobOptions> options,
    ILogger<MessageArchiveJob> logger) : BackgroundService
{
    private static readonly HttpClient Http = new();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(options.Value.Interval);
        do
        {
            try
            {
                await ArchiveAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Message archive cycle failed; retrying next cycle.");
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    internal async Task<int> ArchiveAsync(CancellationToken cancellationToken)
    {
        var now = clock.UtcNow;
        var currentMonthStart = new DateOnly(now.Year, now.Month, 1);
        // `13-08`: the identical per-class cutoff map MessagePartitionPruneJob.PruneAsync builds - both
        // read the same MessagePartitionPruneJobOptions instance, so a class's window can never drift
        // between "archived" and "removed" (this type's own remarks on why it shares that options type
        // at all).
        var cutoffsByClass = RetentionClass.KnownClasses.ToDictionary(
            c => c, c => currentMonthStart.AddMonths(-pruneOptions.Value.EffectiveHorizonMonths(c)));

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);

        var archivedCount = 0;
        var alreadyArchivedByPeriod = new Dictionary<(RetentionClass, DateOnly), IReadOnlySet<Guid>>();

        foreach (var bucketName in MessagePartitionNames.AllBucketNames)
        {
            var slices = await MessagePartitionPruneQuery.ListExpiredSlicesAsync(connection, bucketName, cutoffsByClass, cancellationToken);
            foreach (var slice in slices)
            {
                var periodKey = (slice.RetentionClass, slice.PeriodStart);
                if (!alreadyArchivedByPeriod.TryGetValue(periodKey, out var archivedSiteIds))
                {
                    archivedSiteIds = await archives.ListArchivedSiteIdsAsync(slice.RetentionClass, slice.PeriodStart, cancellationToken);
                    alreadyArchivedByPeriod[periodKey] = archivedSiteIds;
                }

                if (archivedSiteIds.Contains(slice.SiteId))
                {
                    continue;
                }

                try
                {
                    await ArchiveOneAsync(connection, slice, cancellationToken);
                    archivedCount++;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // One site's failure must not stop the rest of this cycle from making progress -
                    // MessagePartitionPruneJob's own gate simply keeps refusing to confirm this slice
                    // until a later cycle succeeds.
                    logger.LogError(
                        ex, "Failed to archive site {SiteId}'s messages for class {RetentionClass}, period {PeriodStart}; will retry next cycle.",
                        slice.SiteId, slice.RetentionClass, slice.PeriodStart);
                }
            }
        }

        if (archivedCount > 0)
        {
            logger.LogInformation("Message archive cycle wrote {Count} archive(s).", archivedCount);
        }

        return archivedCount;
    }

    /// <summary>One site, one period: build the archive onto a local temp file (the identical
    /// stream-to-disk-then-presign-then-upload shape <see cref="SiteExportJob.ProcessExportAsync"/>
    /// already established, for the identical reason - the archive's exact byte count has to be known
    /// before <see cref="IFileStorage.CreateUploadAsync"/> will presign a PUT for it), upload it under
    /// its own prefix, and only *then* record it - <see cref="IMessageArchiveRepository.RecordAsync"/>
    /// runs last, after the upload has already succeeded, which is the one ordering fact
    /// <see cref="MessageArchiveGate"/> depends on to ever answer <see langword="true"/>.</summary>
    private async Task ArchiveOneAsync(NpgsqlConnection connection, ExpiredMessageSlice slice, CancellationToken cancellationToken)
    {
        var archivedAt = clock.UtcNow;
        var tempPath = Path.Combine(Path.GetTempPath(), $"ago-chat-message-archive-{Guid.NewGuid():N}.zip");
        try
        {
            await using (var fileStream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                using var archive = new ZipArchive(fileStream, ZipArchiveMode.Create, leaveOpen: false);
                await archiveWriter.WriteAsync(connection, archive, slice, archivedAt, cancellationToken);
            }

            var length = new FileInfo(tempPath).Length;
            // Its own prefix and, in spirit, its own storage class (`adr/0031`'s Decision 3) - the
            // vendor question of which storage class actually enforces that is out of this item's own
            // scope, so this is a distinct key prefix only, not a distinct bucket or class parameter
            // IFileStorage has no way to express yet. Unchanged in shape by `15-09` - a period is a
            // logical grouping in this key, not a physical partition name, so this key layout still
            // makes sense with no time dimension left in the schema (this item's own Open Questions
            // confirmed this rather than assumed it).
            var objectKey = $"archive/messages/{slice.SiteId:D}/{slice.RetentionClass.Value}/{slice.PeriodStart:yyyy-MM}.zip";

            var upload = await fileStorage.CreateUploadAsync(
                new ObjectKey(objectKey), new UploadConstraints("application/zip", length, options.Value.UploadUrlLifetime), cancellationToken);

            await using (var uploadStream = new FileStream(tempPath, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var content = new StreamContent(uploadStream))
            {
                content.Headers.ContentType = new MediaTypeHeaderValue("application/zip");
                content.Headers.ContentLength = length;
                using var response = await Http.PutAsync(upload.Url, content, cancellationToken);
                response.EnsureSuccessStatusCode();
            }

            // Only now - after the upload is confirmed - is anything recorded. A failure at any point
            // above throws out of this method before reaching here, and MessageArchiveGate simply
            // finds no row for this site next time it looks, exactly the state a never-attempted
            // archive would also be in.
            await archives.RecordAsync(
                idGenerator.NewId(archivedAt), new SiteId(slice.SiteId), slice.RetentionClass, slice.PeriodStart, slice.PeriodEnd,
                objectKey, archivedAt, cancellationToken);
        }
        finally
        {
            try
            {
                if (File.Exists(tempPath))
                {
                    File.Delete(tempPath);
                }
            }
            catch (IOException ex)
            {
                logger.LogWarning(ex, "Could not delete message archive temp file {TempPath}.", tempPath);
            }
        }
    }
}
