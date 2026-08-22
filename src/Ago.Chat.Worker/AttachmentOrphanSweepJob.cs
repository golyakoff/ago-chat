using Ago.Chat.Application.UseCases.CreateAttachment;
using Ago.Platform.Abstractions;
using Ago.Platform.Kernel;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Ago.Chat.Worker;

/// <summary>
/// `5-04`'s orphan sweep: an abandoned upload (presigned, never confirmed) must not leak an object in
/// storage forever (`file-storage.md`'s Upload flow, step 6). Same `PeriodicTimer`/`BackgroundService`
/// shape as `OperatorDisconnectSweepJob` (`4-04`) - runs once immediately, then every
/// <see cref="AttachmentOrphanSweepJobOptions.Interval"/>, and a transient failure logs and retries
/// next tick rather than killing the backstop (`concurrency.md`).
/// </summary>
public sealed class AttachmentOrphanSweepJob(
    NpgsqlDataSource dataSource,
    IFileStorage fileStorage,
    IClock clock,
    AttachmentOptions attachmentOptions,
    IOptions<AttachmentOrphanSweepJobOptions> options,
    ILogger<AttachmentOrphanSweepJob> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(options.Value.Interval);
        do
        {
            try
            {
                await SweepAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Attachment orphan sweep cycle failed; retrying next cycle.");
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    internal async Task SweepAsync(CancellationToken cancellationToken)
    {
        var olderThan = clock.UtcNow - attachmentOptions.UploadLifetime;

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        var claimed = await AttachmentOrphanSweepQuery.ClaimExpiredPendingBatchAsync(
            connection, olderThan, options.Value.BatchSize, cancellationToken);

        foreach (var (id, objectKey) in claimed)
        {
            try
            {
                // Idempotent on the storage side too (5-02's own test: "S3 DELETE is idempotent - no
                // exception expected") - the row is already gone by the time this runs regardless.
                await fileStorage.DeleteAsync(new ObjectKey(objectKey), cancellationToken);
            }
            catch (FileStorageUnavailableException ex)
            {
                logger.LogWarning(
                    ex,
                    "Deleted attachment row {AttachmentId} but could not delete its storage object {ObjectKey}; " +
                    "it may now be an orphan.",
                    id.Value, objectKey);
            }
        }

        if (claimed.Count > 0)
        {
            logger.LogInformation("Attachment orphan sweep removed {Count} expired pending attachment(s).", claimed.Count);
        }
    }
}
