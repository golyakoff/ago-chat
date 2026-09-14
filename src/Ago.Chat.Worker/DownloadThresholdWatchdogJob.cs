using Ago.Chat.Application.Abstractions;
using Ago.Platform.Kernel;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Ago.Chat.Worker;

/// <summary>
/// `25-83`: the soft-threshold warning sweep - "crossing the soft threshold sends an email... proven
/// against a real tenant crossing it, once per crossing, not once per request" (`docs/backlog/25-83-*.md`'s
/// own decision and this item's own "where this is likely to go wrong" both). A scheduled sweep, not
/// an inline send from <c>GetAttachmentDownloadUrlHandler</c> - the identical
/// <see cref="InactivityWatchdogJob"/> shape this class mirrors in full: <c>PeriodicTimer</c>/
/// <c>BackgroundService</c>, raw Npgsql, no Application-layer handler (<see cref="DownloadThresholdWatchdogQuery"/>'s
/// own remarks), one candidate's mail failure logged and retried next cycle rather than stopping the
/// batch.
///
/// <para><b>Why a sweep, not a call from the request path.</b> `GetAttachmentDownloadUrlHandler`
/// already reads the current month's egress and the tier's thresholds on every call (the hard-block
/// gate), so it could in principle detect a fresh soft-threshold crossing itself and send the mail
/// right there. It deliberately does not: an outbound SMTP send is real network I/O this codebase
/// already keeps off every hot request path it can (<c>InactivityWatchdogJob</c>'s own mail is never
/// sent synchronously from a request either), and crossing the soft threshold happens at most once per
/// tenant per month - there is no latency this sweep's own <see cref="DownloadThresholdWatchdogJobOptions.Interval"/>
/// meaningfully trades away by not being instant. The hard block itself stays a live, uncached,
/// request-path read (<c>GetAttachmentDownloadUrlHandler</c>'s own remarks - CLAUDE.md rule 8); only
/// the *notification* moves off it.</para>
///
/// <para><b>The once-per-crossing guarantee is a database compare-and-set, not this job's own
/// bookkeeping.</b> <see cref="DownloadThresholdWatchdogQuery.MarkSoftNotifiedAsync"/>'s own
/// <c>WHERE soft_notified_at IS NULL</c> is what actually prevents a double send - even if two
/// overlapping sweep cycles (a slow batch still running when the next tick fires) both read the same
/// candidate, only one of them will find the row still unmarked and actually receive
/// <c>ExecuteNonQueryAsync</c> returning 1 for it.</para>
/// </summary>
public sealed class DownloadThresholdWatchdogJob(
    NpgsqlDataSource dataSource,
    INotificationMailSender mailSender,
    IClock clock,
    IOptions<DownloadThresholdWatchdogJobOptions> options,
    ILogger<DownloadThresholdWatchdogJob> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(options.Value.Interval);
        do
        {
            try
            {
                await RunOnceAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Download-threshold watchdog cycle failed; retrying next cycle.");
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken)); // runs once immediately, then every Interval
    }

    /// <summary>One bounded pass. <c>internal</c> for the same reason every other job in this project
    /// exposes one - an integration test drives exactly one cycle instead of waiting for a
    /// timer.</summary>
    internal async Task RunOnceAsync(CancellationToken cancellationToken)
    {
        var now = clock.UtcNow;
        var periodMonth = new DateOnly(now.Year, now.Month, 1);

        IReadOnlyList<DownloadThresholdWarningCandidate> candidates;
        await using (var connection = await dataSource.OpenConnectionAsync(cancellationToken))
        {
            candidates = await DownloadThresholdWatchdogQuery.ListSoftThresholdCandidatesAsync(
                connection, periodMonth, options.Value.BatchSize, cancellationToken);
        }

        var warned = 0;
        foreach (var candidate in candidates)
        {
            try
            {
                await WarnSiteAsync(candidate, periodMonth, now, cancellationToken);
                warned++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // One site's mail failure must not stop every other warning in this batch - the same
                // "one candidate's failure logs and moves on" shape InactivityWatchdogJob's own
                // SendWarningsAsync already uses. Not marked notified, so it is reconsidered next
                // cycle rather than silently skipped forever.
                logger.LogError(
                    ex,
                    "Failed to send the download-threshold warning for site {SiteId}; it stays a candidate for the next cycle.",
                    candidate.SiteId);
            }
        }

        if (warned > 0)
        {
            logger.LogInformation("Sent {Count} download-threshold warning(s).", warned);
        }
    }

    private async Task WarnSiteAsync(
        DownloadThresholdWarningCandidate candidate, DateOnly periodMonth, DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<string> recipients;
        await using (var connection = await dataSource.OpenConnectionAsync(cancellationToken))
        {
            recipients = await InactivityWatchdogQuery.ListOperatorEmailsAsync(connection, candidate.SiteId, cancellationToken);
        }

        var (subject, body) = DownloadThresholdWarningMailTemplate.Build(
            candidate.SiteName, candidate.BytesOut, candidate.SoftThresholdBytes, candidate.HardThresholdBytes,
            options.Value.ConsoleUrl);

        foreach (var recipient in recipients)
        {
            await mailSender.SendAsync(new NotificationMailMessage(recipient, subject, body), cancellationToken);
        }

        // Marked notified even when this site has no operator email on file - the identical
        // InactivityWatchdogJob.WarnSiteAsync's own reasoning: retrying a site with nobody to mail
        // forever would never succeed, and the console banner (GetDownloadUsageForSiteHandler, a live
        // read) already shows the warning regardless of whether the mail had anywhere to go.
        await using (var connection = await dataSource.OpenConnectionAsync(cancellationToken))
        {
            await DownloadThresholdWatchdogQuery.MarkSoftNotifiedAsync(
                connection, candidate.SiteId, periodMonth, now, cancellationToken);
        }
    }
}
