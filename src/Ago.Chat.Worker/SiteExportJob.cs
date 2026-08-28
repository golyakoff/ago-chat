using System.IO.Compression;
using System.Net.Http.Headers;
using Ago.Chat.Contracts;
using Ago.Platform.Abstractions;
using Ago.Platform.Kernel;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Ago.Chat.Worker;

/// <summary>
/// `16-03`: builds and uploads one tenant's export archive - the asynchronous half of tenant export,
/// same `PeriodicTimer`/`BackgroundService` shape as <see cref="SiteErasureJob"/>: runs once
/// immediately, then every <see cref="SiteExportJobOptions.Interval"/>, and a transient failure on one
/// request does not stop the others claimed in the same cycle.
///
/// <para><b>The exact-byte-count tension, and how this resolves it.</b> <see cref="IFileStorage.CreateUploadAsync"/>
/// needs the archive's final size before it will presign a PUT at all
/// (<see cref="UploadConstraints.SizeBytes"/> is signed into the URL, exact, not a ceiling), which is
/// a genuine conflict with "streamed, not buffered" if taken to mean "never touches disk either." It
/// does not mean that here: <see cref="ProcessExportAsync"/> streams the archive's construction - row
/// by row, from each store's own <see cref="NpgsqlDataReader"/>, straight into a
/// <see cref="ZipArchive"/> entry (<see cref="SiteExportArchiveWriter"/>'s own remarks) - onto a
/// bounded local temp file, never into a `List`/byte array holding a tenant's full history. Only once
/// that file is complete does this method read its length and presign against it, then stream the
/// upload back out of the same file via <see cref="FileStream"/>/<see cref="StreamContent"/> rather
/// than reading it into memory a second time. The Worker process never holds more than one row, plus
/// whatever the OS's own file-write buffer holds, in memory at any point - it does hold the finished
/// archive on local disk briefly, which is the deliberate, stated trade this makes.</para>
/// </summary>
public sealed class SiteExportJob(
    NpgsqlDataSource dataSource,
    IFileStorage fileStorage,
    SiteExportArchiveWriter archiveWriter,
    IClock clock,
    IOptions<SiteExportJobOptions> options,
    ILogger<SiteExportJob> logger) : BackgroundService
{
    private const string TableTag = "site_export";

    // Ephemeral, immediately-consumed within this job's own process - the same
    // "bare HttpClient, exactly the way a browser would" shape AttachmentThumbnailGenerator already
    // establishes for a Worker-driven presigned-URL transfer (IFileStorage is presign-only, adr/0008).
    private static readonly HttpClient Http = new();

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
                logger.LogError(ex, "Site export cycle failed; retrying next cycle.");
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    /// <summary>One bounded pass. <c>internal</c> for the same reason every other job in this file
    /// exposes one - an integration test drives exactly one cycle instead of waiting for a
    /// timer.</summary>
    internal async Task<int> SweepAsync(CancellationToken cancellationToken)
    {
        var startedAt = clock.UtcNow;

        IReadOnlyList<PendingExport> pending;
        await using (var connection = await dataSource.OpenConnectionAsync(cancellationToken))
        {
            pending = await SiteExportQuery.ListPendingAsync(connection, options.Value.BatchSize, cancellationToken);
        }

        var completed = 0;
        foreach (var item in pending)
        {
            try
            {
                await ProcessExportAsync(item.ExportId, item.SiteId, cancellationToken);
                completed++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(
                    ex, "Failed to build export {ExportId} for site {SiteId}; marking it failed.", item.ExportId, item.SiteId);
                await TryMarkFailedAsync(item.ExportId, ex.Message, cancellationToken);
            }
        }

        if (completed > 0)
        {
            logger.LogInformation("Site export completed {Count} export(s).", completed);
        }

        ChatMetrics.RecordRetentionPruneCycle(TableTag, completed, clock.UtcNow - startedAt);
        return completed;
    }

    /// <summary>
    /// One export request, start to finish: build the archive onto a local temp file, presign and
    /// upload it, mark the request <c>Ready</c>, delete the temp file. Throws on any failure - the
    /// caller (<see cref="SweepAsync"/>) is what marks the request <c>Failed</c>, keeping this method's
    /// own job "produce a ready archive or throw," not "produce a ready archive or quietly write a
    /// failure record," so a bug in this method can never leave a request silently stuck neither
    /// <c>Pending</c> nor resolved.
    ///
    /// <para><b>internal</b>, the same seam every other job in this file exposes, so an integration
    /// test can drive exactly one export against a real Postgres/MinIO instead of waiting for a
    /// timer.</para>
    /// </summary>
    internal async Task ProcessExportAsync(Guid exportId, Guid siteId, CancellationToken cancellationToken)
    {
        var tempPath = Path.Combine(Path.GetTempPath(), $"ago-chat-export-{exportId:N}.zip");
        try
        {
            await using (var connection = await dataSource.OpenConnectionAsync(cancellationToken))
            {
                await using var fileStream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None);
                using var archive = new ZipArchive(fileStream, ZipArchiveMode.Create, leaveOpen: false);
                await archiveWriter.WriteAsync(connection, archive, siteId, clock.UtcNow, cancellationToken);
            }

            var length = new FileInfo(tempPath).Length;
            var objectKey = $"exports/site/{siteId:D}/{exportId:D}.zip";

            var upload = await fileStorage.CreateUploadAsync(
                new ObjectKey(objectKey),
                new UploadConstraints("application/zip", length, options.Value.UploadUrlLifetime),
                cancellationToken);

            await using (var uploadStream = new FileStream(tempPath, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var content = new StreamContent(uploadStream))
            {
                content.Headers.ContentType = new MediaTypeHeaderValue("application/zip");
                content.Headers.ContentLength = length;
                using var response = await Http.PutAsync(upload.Url, content, cancellationToken);
                response.EnsureSuccessStatusCode();
            }

            await using (var connection = await dataSource.OpenConnectionAsync(cancellationToken))
            {
                await SiteExportQuery.MarkReadyAsync(connection, exportId, objectKey, clock.UtcNow, cancellationToken);
            }

            logger.LogInformation(
                "Export {ExportId} for site {SiteId} is ready ({Bytes} bytes).", exportId, siteId, length);
        }
        finally
        {
            // Always attempted, success or failure - a temp file left behind on a failed export is a
            // disk leak with no owner (the row stays Pending or moves to Failed; nothing about either
            // state ever revisits this path). Best-effort: a delete failure here must not mask the
            // real exception this method may already be unwinding with.
            try
            {
                if (File.Exists(tempPath))
                {
                    File.Delete(tempPath);
                }
            }
            catch (IOException ex)
            {
                logger.LogWarning(ex, "Could not delete export temp file {TempPath}.", tempPath);
            }
        }
    }

    private async Task TryMarkFailedAsync(Guid exportId, string reason, CancellationToken cancellationToken)
    {
        try
        {
            await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
            // Truncated: the underlying exception message is not a value this codebase has ever
            // bounded, and failure_reason is a plain text column with no length constraint of its own
            // to lean on.
            var reason1 = reason.Length > 500 ? reason[..500] : reason;
            await SiteExportQuery.MarkFailedAsync(connection, exportId, reason1, clock.UtcNow, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Additionally failed to mark export {ExportId} as failed.", exportId);
        }
    }
}
