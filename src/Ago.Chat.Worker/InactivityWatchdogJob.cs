using Ago.Chat.Application.Abstractions;
using Ago.Chat.Domain;
using Ago.Platform.Abstractions;
using Ago.Platform.Kernel;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Ago.Chat.Worker;

/// <summary>
/// `23-73`: the account-inactivity watchdog's own sweep - two independent passes each tick, both
/// reading <c>sites.last_operator_activity_at</c>/<c>inactivity_warning_sent_at</c>
/// (<see cref="ISiteActivityWatchdog"/>'s own remarks on who writes them):
/// <list type="number">
/// <item><b>Warn.</b> A site whose watchdog fell silent between
/// <see cref="InactivityWatchdogJobOptions.InactivityWindow"/> minus
/// <see cref="InactivityWatchdogJobOptions.WarningLeadTime"/> and the full window ago, not yet warned
/// this cycle, gets the bilingual mail every one of its operators' own registered addresses, and is
/// then marked warned.</item>
/// <item><b>Erase.</b> A site whose watchdog has gone fully silent for the whole window gets
/// <c>22-30</c>'s real erasure mechanism triggered for it - the same
/// <see cref="IErasureRequestRepository.RequestSiteErasureAsync"/> stamp an operator's own
/// <c>RequestSiteErasureHandler</c> call makes, not a second, bespoke deletion path (this item's own
/// brief: reuse <c>22-30</c>, never <c>DemoTenantExpiryJob</c>'s narrower, admittedly-superseded one).
/// <see cref="Ago.Chat.Worker.SiteErasureJob"/>'s own ticks are what actually deletes anything from
/// that point on - this job's whole job is only to *request* it, the identical "deletion is a job, not
/// a request handler" split <c>RequestSiteErasureHandler</c>'s own remarks state.</item>
/// </list>
///
/// <para><b>Why <see cref="OperatorId.System"/>, not
/// <see cref="Ago.Chat.Application.UseCases.RequestSiteErasure.RequestSiteErasureHandler"/>.</b> That
/// handler checks <see cref="IPermissionChecker.HasPermissionAsync"/> against its own
/// <c>RequestedBy</c> - meaningless for a scheduled sweep with no real operator behind it, and a
/// permission check against <see cref="OperatorId.System"/> would simply fail (no
/// <c>operators</c> row exists at that id), silently refusing every erasure this job ever requests.
/// <see cref="Ago.Chat.Application.UseCases.AutoCloseConversation.AutoCloseConversationHandler"/>'s
/// own remarks describe the identical fork for a system-initiated close: a second code path around
/// the operator-facing handler's own authorisation, not a nullable/sentinel operator id threaded
/// through it. This job takes that a step further and does not even need a second handler -
/// <see cref="IErasureRequestRepository"/> itself carries no permission logic at all (that lives one
/// layer up, in <c>RequestSiteErasureHandler</c> alone), so calling the port directly, the same way
/// <see cref="Ago.Chat.Worker.SiteErasureJob"/> already calls <see cref="Ago.Chat.Worker.SiteErasureQuery"/>
/// directly with no intervening handler, is enough.</para>
///
/// <para>Same <see cref="PeriodicTimer"/>/<see cref="BackgroundService"/> shape as every other job in
/// this project: runs once immediately, then every <see cref="InactivityWatchdogJobOptions.Interval"/>,
/// and a transient failure logs and retries next cycle (`concurrency.md`). A fresh
/// <see cref="IServiceScopeFactory"/> scope per erasure candidate, not per tick - the identical
/// "singleton hosted service, scoped dependency" reasoning <see cref="SubscriptionRenewalJob"/>'s own
/// remarks give for <see cref="IErasureRequestRepository"/> being <c>Scoped</c>. The warning half needs
/// no scope at all: <c>NpgsqlDataSource</c> and <see cref="INotificationMailSender"/> are both
/// Singleton-registered (this job's own constructor takes them directly), so nothing about sending a
/// mail or reading/marking a row needs a per-candidate DI scope the way requesting an erasure does.
/// </para>
/// </summary>
public sealed class InactivityWatchdogJob(
    NpgsqlDataSource dataSource,
    IServiceScopeFactory scopeFactory,
    INotificationMailSender mailSender,
    IClock clock,
    IIdGenerator idGenerator,
    IOptions<InactivityWatchdogJobOptions> options,
    ILogger<InactivityWatchdogJob> logger) : BackgroundService
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
                logger.LogError(ex, "Inactivity watchdog cycle failed; retrying next cycle.");
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken)); // runs once immediately, then every Interval
    }

    /// <summary>One bounded pass, both halves. <c>internal</c> for the same reason every other job in
    /// this file exposes one - an integration test drives exactly one cycle instead of waiting for a
    /// timer.</summary>
    internal async Task RunOnceAsync(CancellationToken cancellationToken)
    {
        var now = clock.UtcNow;
        await SendWarningsAsync(now, cancellationToken);
        await RequestErasuresAsync(now, cancellationToken);
    }

    private async Task SendWarningsAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        IReadOnlyList<InactivityWarningCandidate> candidates;
        await using (var connection = await dataSource.OpenConnectionAsync(cancellationToken))
        {
            candidates = await InactivityWatchdogQuery.ListWarningCandidatesAsync(
                connection, now, options.Value.InactivityWindow, options.Value.WarningLeadTime,
                options.Value.BatchSize, cancellationToken);
        }

        var warned = 0;
        foreach (var candidate in candidates)
        {
            try
            {
                await WarnSiteAsync(candidate, now, cancellationToken);
                warned++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // One site's mail failure (an SMTP relay hiccup - EmailSmtpClient's own 4xx/connection-
                // stage throw) must not stop every other warning in this batch - the same "one
                // candidate's failure logs and moves on" shape every other job in this file already
                // uses. Not marked warned, so it is reconsidered next cycle rather than silently
                // skipped forever.
                logger.LogError(
                    ex, "Failed to send inactivity warning for site {SiteId}; it stays a candidate for the next cycle.",
                    candidate.SiteId);
            }
        }

        if (warned > 0)
        {
            logger.LogInformation("Sent {Count} inactivity warning(s).", warned);
        }
    }

    private async Task WarnSiteAsync(
        InactivityWarningCandidate candidate, DateTimeOffset now, CancellationToken cancellationToken)
    {
        IReadOnlyList<string> recipients;
        await using (var connection = await dataSource.OpenConnectionAsync(cancellationToken))
        {
            recipients = await InactivityWatchdogQuery.ListOperatorEmailsAsync(connection, candidate.SiteId, cancellationToken);
        }

        var deletionDate = candidate.EffectiveActivityAt + options.Value.InactivityWindow;
        var daysRemaining = Math.Max(0, (int)Math.Ceiling((deletionDate - now).TotalDays));
        var (subject, body) = InactivityWarningMailTemplate.Build(
            candidate.SiteName, daysRemaining, deletionDate, options.Value.ConsoleLoginUrl);

        foreach (var recipient in recipients)
        {
            await mailSender.SendAsync(new NotificationMailMessage(recipient, subject, body), cancellationToken);
        }

        // Marked warned even when this site has no operator email on file (recipients is empty) -
        // InactivityWatchdogQuery.MarkWarnedAsync's own remarks: retrying a site with nobody to mail
        // forever would never succeed, and the sweep must still eventually erase it on schedule
        // regardless (ListExpiredAsync's own remarks: erasure never depends on the warning having
        // actually reached anyone).
        await using (var connection = await dataSource.OpenConnectionAsync(cancellationToken))
        {
            await InactivityWatchdogQuery.MarkWarnedAsync(connection, candidate.SiteId, now, cancellationToken);
        }
    }

    private async Task RequestErasuresAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        IReadOnlyList<Guid> expired;
        await using (var connection = await dataSource.OpenConnectionAsync(cancellationToken))
        {
            expired = await InactivityWatchdogQuery.ListExpiredAsync(
                connection, now, options.Value.InactivityWindow, options.Value.BatchSize, cancellationToken);
        }

        var requested = 0;
        foreach (var siteId in expired)
        {
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                var erasureRequests = scope.ServiceProvider.GetRequiredService<IErasureRequestRepository>();
                var erasureRecordId = idGenerator.NewId(now);
                await erasureRequests.RequestSiteErasureAsync(
                    new SiteId(siteId), OperatorId.System, erasureRecordId, now, cancellationToken);
                requested++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(
                    ex, "Failed to request erasure for inactive site {SiteId}; it stays a candidate for the next cycle.",
                    siteId);
            }
        }

        if (requested > 0)
        {
            logger.LogInformation("Requested erasure for {Count} inactive site(s) past the {Window} watchdog window.",
                requested, options.Value.InactivityWindow);
        }
    }
}
