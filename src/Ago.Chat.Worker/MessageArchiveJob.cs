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
/// `13-06`/`adr/0031`: writes the archive object <see cref="MessageArchiveGate"/> looks for and
/// <see cref="MessagePartitionPruneJob"/> waits on before it will ever `DROP` a partition - the
/// "nothing is dropped until its archive is confirmed written" half of the ordering this item exists
/// to prove. Runs independently of, and ahead of, that job: reads the identical partition list
/// (<see cref="MessagePartitionPruneQuery.ListPartitionsAsync"/>) against the identical horizon
/// (shares <see cref="MessagePartitionPruneJobOptions.RetentionHorizonMonths"/> rather than a second,
/// independently-configurable number that could silently drift from the one the prune job actually
/// uses), so a partition never becomes a drop candidate before this job has had at least one full cycle
/// to notice it.
///
/// <para><b>One object per site per period</b> (`adr/0031`'s own wording) - a partition holding several
/// tenants' messages produces several archive objects, one per distinct <c>site_id</c> its rows carry,
/// each recorded as its own <see cref="IMessageArchiveRepository"/> row. A site already recorded for a
/// given (class, period) is skipped without re-reading its rows - <see cref="IMessageArchiveGate"/>'s
/// own "gap" query is reused here as the cheap "is there anything left to do" check before this job
/// does the more expensive per-site enumeration, so a fully-archived partition costs one query per
/// cycle, not one per site.</para>
///
/// <para><b>Never touches an unattributed row.</b> Only <c>site_id IS NOT NULL</c> rows are ever
/// candidates - a row `MessageSiteIdBackfillJob` has not reached yet is invisible to this job exactly
/// as it is invisible to <see cref="MessageArchiveGate"/>'s own confirmation query, so the two agree by
/// construction about when a partition is genuinely, completely done.</para>
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
        var cutoff = currentMonthStart.AddMonths(-pruneOptions.Value.RetentionHorizonMonths);

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        var partitions = await MessagePartitionPruneQuery.ListPartitionsAsync(connection, cancellationToken);

        var archivedCount = 0;
        foreach (var partition in partitions)
        {
            if (partition.PeriodEnd > cutoff)
            {
                continue; // Same horizon MessagePartitionPruneJob uses - nothing to do yet.
            }

            var pendingSiteIds = await ListSitesPendingArchiveAsync(connection, partition, cancellationToken);
            foreach (var siteId in pendingSiteIds)
            {
                try
                {
                    await ArchiveOneAsync(connection, partition, siteId, cancellationToken);
                    archivedCount++;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // One site's failure must not stop the rest of this partition, or the next
                    // partition, from making progress - MessagePartitionPruneJob's own gate simply
                    // keeps refusing to confirm this partition until a later cycle succeeds.
                    logger.LogError(
                        ex, "Failed to archive site {SiteId}'s messages for partition {Partition}; will retry next cycle.",
                        siteId, partition.Name);
                }
            }
        }

        if (archivedCount > 0)
        {
            logger.LogInformation("Message archive cycle wrote {Count} archive(s).", archivedCount);
        }

        return archivedCount;
    }

    /// <summary>Distinct, attributed site ids this partition's own rows carry, minus whichever ones
    /// already have a <see cref="IMessageArchiveRepository"/> row for this exact (class, period) -
    /// the per-partition work list, computed fresh every cycle from the live partition rather than
    /// cached, so a site whose first attempt failed is retried automatically.</summary>
    private async Task<IReadOnlyList<Guid>> ListSitesPendingArchiveAsync(
        NpgsqlConnection connection, MessagePartitionInfo partition, CancellationToken cancellationToken)
    {
        var alreadyArchived = await archives.ListArchivedSiteIdsAsync(partition.RetentionClass, partition.PeriodStart, cancellationToken);

        var sql = $"SELECT DISTINCT site_id FROM {partition.Name} WHERE site_id IS NOT NULL";
        await using var command = new NpgsqlCommand(sql, connection);

        var pending = new List<Guid>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var siteId = reader.GetGuid(0);
            if (!alreadyArchived.Contains(siteId))
            {
                pending.Add(siteId);
            }
        }

        return pending;
    }

    /// <summary>One site, one period: build the archive onto a local temp file (the identical
    /// stream-to-disk-then-presign-then-upload shape <see cref="SiteExportJob.ProcessExportAsync"/>
    /// already established, for the identical reason - the archive's exact byte count has to be known
    /// before <see cref="IFileStorage.CreateUploadAsync"/> will presign a PUT for it), upload it under
    /// its own prefix, and only *then* record it - <see cref="IMessageArchiveRepository.RecordAsync"/>
    /// runs last, after the upload has already succeeded, which is the one ordering fact
    /// <see cref="MessageArchiveGate"/> depends on to ever answer <see langword="true"/>.</summary>
    private async Task ArchiveOneAsync(
        NpgsqlConnection connection, MessagePartitionInfo partition, Guid siteId, CancellationToken cancellationToken)
    {
        var archivedAt = clock.UtcNow;
        var tempPath = Path.Combine(Path.GetTempPath(), $"ago-chat-message-archive-{Guid.NewGuid():N}.zip");
        try
        {
            await using (var fileStream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                using var archive = new ZipArchive(fileStream, ZipArchiveMode.Create, leaveOpen: false);
                await archiveWriter.WriteAsync(
                    connection, archive, partition.Name, siteId, partition.RetentionClass,
                    partition.PeriodStart, partition.PeriodEnd, archivedAt, cancellationToken);
            }

            var length = new FileInfo(tempPath).Length;
            // Its own prefix and, in spirit, its own storage class (`adr/0031`'s Decision 3) - the
            // vendor question of which storage class actually enforces that is out of this item's own
            // scope (backlog: "constrained by 16-01's residency rule... the same open vendor question
            // 15-02 already carries"), so this is a distinct key prefix only, not a distinct bucket or
            // class parameter IFileStorage has no way to express yet.
            var objectKey = $"archive/messages/{siteId:D}/{partition.RetentionClass.Value}/{partition.PeriodStart:yyyy-MM}.zip";

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
                idGenerator.NewId(archivedAt), new SiteId(siteId), partition.RetentionClass, partition.PeriodStart, partition.PeriodEnd,
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
