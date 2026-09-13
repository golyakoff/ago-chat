using Ago.Chat.Application.Abstractions;
using Ago.Chat.Application.Mapping;
using Ago.Chat.Infrastructure.Postgres;
using Ago.Chat.Infrastructure.Postgres.Persistence;
using Ago.Platform.Abstractions;
using Ago.Platform.Kernel;
using Ago.Platform.Persistence.Postgres;

namespace Ago.Chat.Worker;

/// <summary>
/// `22-08`/`adr/0149` rule 1: "chat renews it on a schedule, at half the lease length, so a single
/// missed renewal expires nothing." This is the renewal - the one loop that keeps every currently
/// suspended site's calendar-side lease alive, republishing <c>TenantSuspensionChanged</c> on
/// <see cref="SuspensionLeaseOptions.RenewalInterval"/>'s own cadence for as long as
/// <see cref="ISiteSuspensionReadStore.ListActiveSuspensionsAsync"/> keeps naming the site.
///
/// <para><b>A suspension is chat <em>declining</em> to renew, not a job that notices expiry.</b> The
/// moment a site's own owner-facing <c>suspended_until</c> passes (self-expiry) or is lifted, this
/// job's own read simply stops naming it - there is no separate "detect expiry and publish a closing
/// event" branch here, because <see cref="ISiteSuspensionReadStore"/>'s live comparison already is
/// that detection, and an explicit lift already publishes its own immediate correction
/// (<c>LiftSuspensionAsOwnerHandler</c>). This job's only job is keeping the lease topped up while a
/// site is suspended, never announcing that it stopped.</para>
///
/// <para><b>Same <see cref="PeriodicTimer"/>/<see cref="BackgroundService"/> shape as
/// <see cref="DemoTenantExpiryJob"/></b> - runs once immediately, then on
/// <see cref="SuspensionLeaseOptions.RenewalInterval"/>, and a transient failure logs and retries next
/// tick rather than killing the loop. Retrying is safe because every publish is idempotent by
/// construction: <c>TenantSuspensionChanged</c> is a snapshot of "suspended until this instant", and
/// republishing the identical (or a slightly later) instant for a site already known to be suspended
/// changes nothing a consumer could observe as wrong.</para>
///
/// <para><b>Resolves its own scope's <see cref="AgoChatDbContext"/> directly, the same "a host may
/// construct the DbContext and stage the outbox itself" latitude <see cref="IUnitOfWork"/>'s own
/// remarks grant <c>OperatorConversationReleaser</c></b> - this is a Worker host loop, not a per-request
/// Application handler, and every currently-suspended site's own renewal is staged onto one
/// <see cref="AgoChatDbContext.SaveChangesAsync"/> per tick rather than one transaction per site: there
/// is no cross-aggregate invariant to protect between two different sites' own renewals, only the
/// ordinary "the outbox row and nothing else" write every other publish-only job already makes.</para>
/// </summary>
public sealed class SuspensionLeaseRenewalJob(
    IServiceScopeFactory scopeFactory,
    SuspensionLeaseOptions options,
    IClock clock,
    IIdGenerator idGenerator,
    ILogger<SuspensionLeaseRenewalJob> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(options.RenewalInterval);
        do
        {
            try
            {
                await RenewAllAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Suspension lease renewal cycle failed; retrying next cycle.");
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    /// <summary>One bounded pass. <c>internal</c> so an integration test can drive exactly one cycle
    /// instead of waiting for the timer - the same seam <see cref="DemoTenantExpiryJob.SweepAsync"/>
    /// already exposes for the identical reason, and the seam the broker-stopped test uses to establish
    /// an initial lease before stopping the broker.</summary>
    internal async Task<int> RenewAllAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var suspensions = scope.ServiceProvider.GetRequiredService<ISiteSuspensionReadStore>();
        var db = scope.ServiceProvider.GetRequiredService<AgoChatDbContext>();
        var outbox = new EfOutboxWriter<AgoChatDbContext>(db);

        var now = clock.UtcNow;
        var activeSiteIds = await suspensions.ListActiveSuspensionsAsync(now, cancellationToken);
        if (activeSiteIds.Count == 0)
        {
            return 0;
        }

        var leaseUntil = now + options.LeaseLength;
        foreach (var siteId in activeSiteIds)
        {
            outbox.Enqueue(TenantSuspensionChangedMapper.ToEnvelope(siteId, leaseUntil, now, idGenerator));
        }

        await db.SaveChangesAsync(cancellationToken);

        logger.LogInformation(
            "Renewed the suspension lease for {Count} currently-suspended site(s), until {LeaseUntil:O}.",
            activeSiteIds.Count, leaseUntil);

        return activeSiteIds.Count;
    }
}
