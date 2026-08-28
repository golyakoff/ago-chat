using Ago.Chat.Application.Abstractions;
using Ago.Chat.Application.Caching;
using Ago.Chat.Contracts;
using Ago.Chat.Domain;
using Ago.Platform.Caching.Redis;
using Ago.Platform.Kernel;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Ago.Chat.Worker;

/// <summary>
/// `16-02`: the whole-account counterpart to <see cref="ConversationErasureJob"/> - drains a site's
/// conversations first (via that job's own independent ticks, deliberately, see below), then removes
/// its operators, its Keycloak identities and finally the site row itself.
///
/// <para><b>Why this job stamps conversations and waits, rather than driving
/// <c>ConversationErasureJob.EraseConversationAsync</c> directly in a loop within its own tick.</b>
/// Both are defensible (`16-02`'s own brief says so); this one is chosen because driving it directly
/// would need <c>ConversationErasureJob</c> resolvable as a plain dependency here, on top of its
/// existing `AddHostedService` registration - solvable, but at the cost of a second registration and a
/// cross-job coupling for a benefit that is only latency, and erasure is asynchronous by contract
/// already (the HTTP endpoint answers `202 Accepted` before any deletion happens at all). Relying on
/// <see cref="ConversationErasureJob"/>'s own ticks costs at most a few extra
/// <see cref="ConversationErasureJobOptions.Interval"/>s before a site's conversations finish draining
/// - immaterial next to a process that is already polled for completion rather than awaited
/// synchronously - and keeps the two jobs decoupled: neither needs to know the other's constructor
/// shape, only the columns and rows they both read and write.</para>
///
/// Same `PeriodicTimer`/`BackgroundService` shape as every other job in this file.
/// </summary>
public sealed class SiteErasureJob(
    NpgsqlDataSource dataSource,
    IDemoIdentityProvisioner identities,
    CacheInvalidationPublisher cacheInvalidation,
    IIdGenerator idGenerator,
    IClock clock,
    IOptions<SiteErasureJobOptions> options,
    ILogger<SiteErasureJob> logger) : BackgroundService
{
    private const string TableTag = "sites_erasure";

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
                logger.LogError(ex, "Site erasure cycle failed; retrying next cycle.");
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    /// <summary>One bounded pass. <c>internal</c> for the same reason every other job in this file
    /// exposes one - an integration test drives exactly one cycle instead of waiting for a
    /// timer.</summary>
    internal async Task<int> SweepAsync(CancellationToken cancellationToken)
    {
        var startedAt = clock.UtcNow;

        IReadOnlyList<Guid> pending;
        await using (var connection = await dataSource.OpenConnectionAsync(cancellationToken))
        {
            pending = await SiteErasureQuery.ListPendingAsync(connection, options.Value.BatchSize, cancellationToken);
        }

        var erased = 0;
        foreach (var siteId in pending)
        {
            try
            {
                if (await ProcessSiteAsync(siteId, cancellationToken))
                {
                    erased++;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(
                    ex, "Failed to process site {SiteId} for erasure; it stays flagged for the next cycle.", siteId);
            }
        }

        if (erased > 0)
        {
            logger.LogInformation("Site erasure removed {Count} site(s) and everything under them.", erased);
        }

        ChatMetrics.RecordRetentionPruneCycle(TableTag, erased, clock.UtcNow - startedAt);
        return erased;
    }

    /// <summary>
    /// One site, one tick: (a) idempotently stamp every conversation that does not carry the flag yet,
    /// (b) bail out this tick if any conversation still exists - <see cref="ConversationErasureJob"/>'s
    /// own ticks are what drains them - and only once none remain does this method (c) collect operator
    /// subject ids, delete the site row (cascading operators/roles/operator_roles/visitors/
    /// channel_identities/webhook_endpoints/webhook_deliveries - <see cref="SiteErasureQuery.DeleteSiteAsync"/>'s
    /// own remarks), invalidate the cached site config under both keys, and finally delete each
    /// Keycloak user - last, for the identical reason `DemoTenantExpiryJob.RemoveAsync`'s own remarks
    /// give: it is the step most likely to fail and the least harmful to leave for a retry, since the
    /// sweeper re-finds nothing more to retry once the site row itself is gone - but the identities are
    /// collected *before* that delete runs, precisely so they are still known afterward.
    /// </summary>
    /// <returns><see langword="true"/> if the site was fully erased this call.</returns>
    internal async Task<bool> ProcessSiteAsync(Guid siteId, CancellationToken cancellationToken)
    {
        var now = clock.UtcNow;

        await using (var connection = await dataSource.OpenConnectionAsync(cancellationToken))
        {
            await SiteErasureQuery.StampConversationsAsync(connection, siteId, now, cancellationToken);

            if (await SiteErasureQuery.HasAnyConversationAsync(connection, siteId, cancellationToken))
            {
                // Not an error and not a stall: ConversationErasureJob's own independent ticks are
                // draining these in bounded batches. Nothing more for this tick to do for this site.
                return false;
            }
        }

        string? publicKey;
        IReadOnlyList<string> subjectIds;
        await using (var connection = await dataSource.OpenConnectionAsync(cancellationToken))
        {
            // Read before the delete: `14-04`'s own two-key shape (ForPublicKey for the widget
            // handshake path, ForSiteId for anything holding a JWT's site_id claim) needs the public
            // key to build the first one, and it cannot be reconstructed once the row naming it is
            // gone. Unlike the MinIO object-store ordering elsewhere in this item, reading it ahead of
            // the delete carries no orphan risk - a cache key lookup is never the thing that makes
            // bytes unreachable.
            publicKey = await SiteErasureQuery.GetPublicKeyAsync(connection, siteId, cancellationToken);
            subjectIds = await SiteErasureQuery.ListOperatorSubjectIdsAsync(connection, siteId, cancellationToken);
            await SiteErasureQuery.DeleteSiteAsync(connection, siteId, cancellationToken);
        }

        // Both keys, invalidated only after the delete commits - so a request racing this invalidation
        // finds nothing in the database to repopulate the cache with, rather than a window in which
        // eviction and a stale reload could interleave.
        if (publicKey is not null)
        {
            await cacheInvalidation.PublishAsync(SiteCacheKeys.ForPublicKey(publicKey), idGenerator.NewId(now), cancellationToken);
        }

        await cacheInvalidation.PublishAsync(SiteCacheKeys.ForSiteId(new SiteId(siteId)), idGenerator.NewId(now), cancellationToken);

        foreach (var subjectId in subjectIds)
        {
            await identities.DeleteAsync(subjectId, cancellationToken);
        }

        logger.LogInformation(
            "Site {SiteId} erased: the site subtree and {IdentityCount} identity-provider user(s).",
            siteId, subjectIds.Count);
        return true;
    }
}
