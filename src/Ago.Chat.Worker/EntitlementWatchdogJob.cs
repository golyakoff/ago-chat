using Ago.Chat.Application.Abstractions;
using Ago.Chat.Application.UseCases;
using Ago.Chat.Application.UseCases.OperatorRoleSeats;
using Ago.Chat.Domain;
using Ago.Platform.Kernel;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Ago.Chat.Worker;

/// <summary>
/// `25-170`: "a tenant can run past what it is entitled to, with nothing noticing" - the recurring,
/// one-minute-cadence catch-all this item's own design calls for, doing two independent things per site:
///
/// <list type="number">
/// <item><b>Role-capacity reconciliation.</b> For every site, for both seeded roles (`"Operator"`,
/// `"Admin"`), <see cref="OperatorRoleSeatReconciler"/> disables the excess held-seat holder(s) if either
/// role's own live count now exceeds its own `Site` limit - catching a subscription downgrade the moment
/// the next tick runs, not only the instant <c>SubscriptionRenewalApplier</c>'s own transactional
/// fast-path call happens to fire.</item>
/// <item><b>Channel-entitlement reconciliation.</b> For every active <see cref="Domain.ChannelCredential"/>
/// across every <see cref="ChannelKind"/>, <see cref="ChannelEntitlement.IsEntitledAsync"/> re-reads
/// <see cref="Domain.ModuleQuantityGrant.EffectiveQuantity"/> live; a credential whose entitlement has
/// dropped to zero is paused (<see cref="Domain.ChannelCredential.PauseForLapsedEntitlement"/> - the
/// ambient long-polling loop for that channel/site stops within this job's own next tick, see
/// <c>TelegramLongPollingService</c>/<c>MaxLongPollingService</c>'s own `RefreshPollersAsync` for where
/// that pause is actually read), and one whose entitlement has since come back is resumed
/// (<see cref="Domain.ChannelCredential.ResumeAfterEntitlementRestored"/>) - both directions, since this
/// is a reversible, read-time fact, never a one-way disconnect (`Domain.ChannelCredential.RevokeForLapsedEntitlement`'s
/// own, deliberately separate, human-reviewed mechanism handles the one-way case, `adr/0151`).</item>
/// </list>
///
/// <para><b>One job, not two, sharing this file's own cadence</b> - the author's own framing ("doing two
/// independent things per site"), kept as one `BackgroundService` rather than split: neither half depends
/// on the other's result, and nothing about either half's own failure mode benefits from a separate timer.</para>
///
/// <para><b>A fresh <see cref="IServiceScopeFactory"/> scope per site (role-capacity) and per credential
/// (channel-entitlement), not per tick.</b> The identical reasoning <c>SubscriptionRenewalJob</c>'s own
/// remarks give: this class is a singleton hosted service, but every port it calls through is scoped -
/// one shared scope across a whole tick would share one `DbContext` change tracker across every site (or
/// credential) in that tick, silently serving a stale read to the second one onward.</para>
///
/// <para><b>Role-capacity reconciliation gets its own transaction per site</b> (<see cref="IUnitOfWork"/>) -
/// <see cref="IOperatorRoleRepository.LockAndGetHeldSeatHolderIdsAsync"/>'s own contract requires an
/// ambient transaction for its row lock to mean anything (`OperatorRoleSeatReconciler`'s own remarks).
/// Channel-entitlement reconciliation needs no transaction of its own - each credential's own
/// pause/resume is a single-aggregate save with no invariant spanning more than one row.</para>
/// </summary>
public sealed class EntitlementWatchdogJob(
    IServiceScopeFactory scopeFactory,
    NpgsqlDataSource dataSource,
    IClock clock,
    IOptions<EntitlementWatchdogJobOptions> options,
    ILogger<EntitlementWatchdogJob> logger) : BackgroundService
{
    /// <summary>`25-170`: the two seeded roles that carry a seat concept at all - the same pair
    /// `GetSeatAssignmentSummaryHandler`'s own remarks enumerate for its own analogous read.</summary>
    private static readonly string[] RoleNames = ["Operator", "Admin"];

    /// <summary>`25-181`: the identical role-name pairing `Ago.Chat.Application.UseCases.OperatorRoleSeats.RoleSeatLimits.OwnerGrantRoleFor`
    /// draws, restated here as this job's own tiny local copy rather than reaching into that class - it
    /// is `internal` to `Ago.Chat.Application`, and this job already keeps its own local copy of
    /// <see cref="RoleNames"/> itself for the identical reason (no `InternalsVisibleTo` grant exists from
    /// that assembly to this one, and adding one for two bare string literals would be a wider seam than
    /// the duplication it would save).</summary>
    private static OwnerSeatGrantRole OwnerGrantRoleFor(string roleName) => roleName switch
    {
        "Operator" => OwnerSeatGrantRole.Operator,
        "Admin" => OwnerSeatGrantRole.Administrator,
        _ => throw new ArgumentOutOfRangeException(nameof(roleName), roleName, $"No owner seat grant role is defined for role '{roleName}'."),
    };

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
                logger.LogError(ex, "Entitlement watchdog cycle failed; retrying next cycle.");
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken)); // runs once immediately, then every Interval
    }

    /// <summary><c>internal</c> so an integration test can drive exactly one cycle instead of waiting
    /// for a timer - the same seam every other job in this project exposes for the same reason.</summary>
    internal async Task RunOnceAsync(CancellationToken cancellationToken)
    {
        await ReconcileRoleSeatsAsync(cancellationToken);
        await ReconcileChannelEntitlementsAsync(cancellationToken);
    }

    private async Task ReconcileRoleSeatsAsync(CancellationToken cancellationToken)
    {
        var siteIds = await GetAllSiteIdsAsync(cancellationToken);
        var disabledTotal = 0;

        foreach (var siteId in siteIds)
        {
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
                var sites = scope.ServiceProvider.GetRequiredService<ISiteRepository>();
                var reconciler = scope.ServiceProvider.GetRequiredService<OperatorRoleSeatReconciler>();
                var ownerSeatGrants = scope.ServiceProvider.GetRequiredService<IOwnerSeatGrantStore>();

                await using var transaction = await unitOfWork.BeginTransactionAsync(cancellationToken);

                var site = await sites.GetByIdAsync(siteId, cancellationToken);
                if (site is null)
                {
                    // The site list was read a moment ago on a different connection - vanishingly
                    // unlikely (this codebase has no site-deletion path at all today), but a watchdog
                    // sweep must not throw over a row that stopped existing between its own two reads.
                    continue;
                }

                foreach (var roleName in RoleNames)
                {
                    // `25-181`: the platform owner's own hand-granted extra, resolved live against this
                    // tick's own `clock.UtcNow` - the one thing that makes an owner grant's expiry
                    // "checked live" rather than a stored fact that goes stale: the very next tick after
                    // it lapses resolves 0 here instead of whatever it used to, and the reconciler
                    // demotes the excess exactly as it already would for a billing-driven drop, with no
                    // second, bespoke consequence built for the identical shape of problem.
                    var extraCapacity = await ownerSeatGrants.GetEffectiveExtraAsync(
                        siteId, OwnerGrantRoleFor(roleName), clock.UtcNow, cancellationToken);
                    disabledTotal += await reconciler.ReconcileAsync(site, roleName, extraCapacity, cancellationToken);
                }

                await transaction.CommitAsync(cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // One site's contention or transient failure must not stop every other site this tick -
                // the same "one candidate's failure logs and moves on" shape `SubscriptionRenewalJob`'s
                // own remarks already establish.
                logger.LogError(ex, "Role-seat reconciliation failed for site {SiteId}; retrying next tick.", siteId.Value);
            }
        }

        if (disabledTotal > 0)
        {
            logger.LogInformation("Disabled {Count} role-seat holder(s) over their site's own capacity.", disabledTotal);
        }
    }

    private async Task ReconcileChannelEntitlementsAsync(CancellationToken cancellationToken)
    {
        var paused = 0;
        var resumed = 0;

        foreach (var kind in Enum.GetValues<ChannelKind>())
        {
            IReadOnlyList<ChannelCredentialId> activeIds;
            await using (var listScope = scopeFactory.CreateAsyncScope())
            {
                var credentials = listScope.ServiceProvider.GetRequiredService<IChannelCredentialRepository>();
                var active = await credentials.GetAllActiveAsync(kind, cancellationToken);
                activeIds = active.Select(c => c.Id).ToList();
            }

            foreach (var credentialId in activeIds)
            {
                try
                {
                    await using var scope = scopeFactory.CreateAsyncScope();
                    var credentials = scope.ServiceProvider.GetRequiredService<IChannelCredentialRepository>();
                    var entitlements = scope.ServiceProvider.GetRequiredService<IBillingOptionEntitlementProvider>();
                    var grants = scope.ServiceProvider.GetRequiredService<IModuleQuantityGrantStore>();

                    var credential = await credentials.GetByIdAsync(credentialId, cancellationToken);
                    if (credential is null || !credential.Active)
                    {
                        // Revoked or gone between the list read above and now - nothing left to pause
                        // or resume.
                        continue;
                    }

                    var entitled = await ChannelEntitlement.IsEntitledAsync(
                        entitlements, grants, credential.SiteId, kind, cancellationToken);

                    if (!entitled && credential.EntitlementPausedAt is null)
                    {
                        credential.PauseForLapsedEntitlement(clock.UtcNow);
                        await credentials.SaveAsync(credential, cancellationToken);
                        paused++;
                    }
                    else if (entitled && credential.EntitlementPausedAt is not null)
                    {
                        credential.ResumeAfterEntitlementRestored();
                        await credentials.SaveAsync(credential, cancellationToken);
                        resumed++;
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogError(
                        ex, "Channel-entitlement reconciliation failed for credential {ChannelCredentialId}; retrying next tick.",
                        credentialId.Value);
                }
            }
        }

        if (paused > 0 || resumed > 0)
        {
            logger.LogInformation(
                "Paused {Paused} channel credential(s) with a lapsed entitlement, resumed {Resumed} whose entitlement returned.",
                paused, resumed);
        }
    }

    /// <summary>Every site on this deployment - a plain, unindexed scan, the identical judgement
    /// `ConversationAssignmentJob`'s own remarks make for its own site enumeration (`GetSiteIdsWithWaitingConversationsAsync`):
    /// a full table scan of `sites` is cheap at this project's own scale, and adding an index for a
    /// once-a-minute background sweep over every row would be solving a problem this deployment does
    /// not have.</summary>
    private async Task<IReadOnlyList<SiteId>> GetAllSiteIdsAsync(CancellationToken cancellationToken)
    {
        const string sql = "SELECT id FROM sites";

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);

        var siteIds = new List<SiteId>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            siteIds.Add(new SiteId(reader.GetGuid(0)));
        }

        return siteIds;
    }
}
