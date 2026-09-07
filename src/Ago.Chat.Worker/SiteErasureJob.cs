using Ago.Chat.Application.Abstractions;
using Ago.Chat.Application.Caching;
using Ago.Chat.Contracts;
using Ago.Chat.Domain;
using Ago.Platform.Abstractions;
using Ago.Platform.Caching.Redis;
using Ago.Platform.Kernel;
using Microsoft.Extensions.DependencyInjection;
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
    IFileStorage fileStorage,
    IMessageArchiveRepository archives,
    CacheInvalidationPublisher cacheInvalidation,
    IIdGenerator idGenerator,
    IClock clock,
    IServiceScopeFactory scopeFactory,
    IModuleProvisioningSecretProvider provisioningSecrets,
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

        IReadOnlyList<PendingSiteErasure> pending;
        await using (var connection = await dataSource.OpenConnectionAsync(cancellationToken))
        {
            pending = await SiteErasureQuery.ListPendingAsync(connection, options.Value.BatchSize, cancellationToken);
        }

        var erased = 0;
        foreach (var candidate in pending)
        {
            try
            {
                if (await ProcessSiteAsync(
                        candidate.SiteId, candidate.ErasureRecordId, candidate.RequestedAt, cancellationToken))
                {
                    erased++;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(
                    ex, "Failed to process site {SiteId} for erasure; it stays flagged for the next cycle.",
                    candidate.SiteId);
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
    /// own ticks are what drains them, and (`24-09`) each one that drains has already had its own rows
    /// stripped out of every archive object the site has, via that job's own
    /// <see cref="ConversationArchiveEraser"/> step - and only once none remain does this method
    /// (c) collect operator subject ids, (d) delete each of this site's own <c>message_archives</c>
    /// objects from storage (`24-09` - see below), (e) delete the site row (cascading operators/roles/
    /// operator_roles/visitors/channel_identities/webhook_endpoints/webhook_deliveries/
    /// <c>message_archives</c> itself - <see cref="SiteErasureQuery.DeleteSiteAsync"/>'s own remarks),
    /// invalidate the cached site config under both keys, and finally delete each Keycloak user - last,
    /// for the identical reason `DemoTenantExpiryJob.RemoveAsync`'s own remarks give: it is the step
    /// most likely to fail and the least harmful to leave for a retry, since the sweeper re-finds
    /// nothing more to retry once the site row itself is gone - but the identities are collected
    /// *before* that delete runs, precisely so they are still known afterward.
    ///
    /// <para><b>`24-09`: why the archive objects are deleted here rather than left to the row's own
    /// cascade.</b> <c>message_archives</c> cascades with the site the same as every other table in
    /// (e)'s list, but that only removes the manifest *row* - the `.zip` object it names is a separate
    /// thing in object storage that no foreign key reaches. By the time this method gets here, step
    /// (b)'s own gate guarantees every conversation this site ever had has been drained by
    /// <see cref="ConversationErasureJob"/>, which means every one of them has already had its own
    /// content stripped from every period this site has archived - so each archive object left standing
    /// holds no message content at all, only an empty (or untouched-because-genuinely-empty) manifest
    /// and jsonl shell. Deleting the object here removes that shell outright, rather than leaving
    /// `personal-data.md`'s previously-named gap ("the archive .zip is then orphaned in storage") to
    /// recur in a smaller form. The delete is read-before-delete (<see cref="IMessageArchiveRepository.ListForSiteAsync"/>
    /// runs first, since the row naming an object is gone the moment (e) runs) and tolerant of a storage
    /// failure - logged, not thrown - because by construction there is no personal data left in these
    /// objects for a failure here to expose; an orphaned empty shell is a smaller and different problem
    /// than the one this item exists to close.</para>
    /// </summary>
    /// <returns><see langword="true"/> if the site was fully erased this call.</returns>
    /// <remarks>
    /// `24-13`: <paramref name="erasureRecordId"/> is this site's own pointer to its `erasure_records`
    /// receipt, threaded through to every <see cref="ErasureRecordQuery"/> call below (all no-ops when
    /// it is <see langword="null"/>, though in practice it never is for a site - see
    /// <see cref="PendingSiteErasure"/>'s own remarks). The whole method is now wrapped in one
    /// try/catch, the same shape <see cref="ConversationErasureJob.EraseConversationAsync"/>'s own
    /// remarks explain: a throw anywhere in here marks the receipt <c>Failed</c> before rethrowing, so
    /// <see cref="SweepAsync"/>'s own catch still logs and retries next cycle exactly as before.
    /// </remarks>
    internal async Task<bool> ProcessSiteAsync(
        Guid siteId, Guid? erasureRecordId, DateTimeOffset requestedAt, CancellationToken cancellationToken)
    {
        var now = clock.UtcNow;
        try
        {
            await using (var connection = await dataSource.OpenConnectionAsync(cancellationToken))
            {
                var newlyStamped = await SiteErasureQuery.StampConversationsAsync(connection, siteId, now, cancellationToken);
                await ErasureRecordQuery.AddConversationsMarkedAsync(connection, erasureRecordId, newlyStamped, cancellationToken);

                if (await SiteErasureQuery.HasAnyConversationAsync(connection, siteId, cancellationToken))
                {
                    // Not an error and not a stall: ConversationErasureJob's own independent ticks are
                    // draining these in bounded batches. Nothing more for this tick to do for this site.
                    return false;
                }
            }

            // `22-30`/`adr/0149` rule 2: the module gate. Every module this site has ever had - active,
            // lapsed or revoked, none of which delete the row any more (EnabledModule.RevokedAt's own
            // remarks) - must *prove* it holds nothing left for this tenant before the site row (and
            // with it, the address of every one of those modules) is allowed to disappear. See
            // EraseModulesAsync's own remarks for the full ordering argument.
            if (!await EraseModulesAsync(siteId, erasureRecordId, requestedAt, now, cancellationToken))
            {
                return false;
            }

            // `24-09`: read every archive object this site has *before* the site row goes - message_archives
            // cascades with sites (MessageArchiveEntityConfiguration's own remarks), so the object keys are
            // unreachable the instant DeleteSiteAsync below commits. Every conversation this site had is
            // already drained by this point (the HasAnyConversationAsync gate above), so each of these
            // objects has already been stripped of message content by ConversationErasureJob's own
            // ConversationArchiveEraser step - what is deleted here is an empty shell, not personal data.
            var archiveRecords = await archives.ListForSiteAsync(new SiteId(siteId), cancellationToken);
            var archiveObjectsDeleted = 0;
            foreach (var archiveRecord in archiveRecords)
            {
                try
                {
                    await fileStorage.DeleteAsync(new ObjectKey(archiveRecord.ObjectKey), cancellationToken);
                    archiveObjectsDeleted++;
                }
                catch (FileStorageUnavailableException ex)
                {
                    // Tolerated, not swallowed silently: by construction this object no longer holds any
                    // conversation's content (every one of the site's conversations already went through
                    // ConversationArchiveEraser), so the residual is an orphaned empty shell, the same
                    // "storage hiccup must not abandon the whole erasure" tolerance
                    // ConversationErasureJob's own attachment-object delete already applies. Not counted
                    // as deleted either - the same "the receipt must not claim bytes are gone that are
                    // not" reasoning ConversationErasureJob's own remarks give.
                    logger.LogWarning(
                        ex, "Could not delete archive object {ObjectKey} for site {SiteId} being erased; it may now be an orphan.",
                        archiveRecord.ObjectKey, siteId);
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
                // `24-13`: the receipt, before the row - identities below are collected already
                // (subjectIds) but not yet deleted, and this job's own remarks already accept that a
                // failure deleting them cannot be retried once the site row is gone (nothing left for
                // ListPendingAsync to reclaim). The count recorded here is therefore the count about to
                // be attempted, the same optimism this method's own closing log line already carried
                // before this item existed.
                await ErasureRecordQuery.CompleteSiteErasureAsync(
                    connection, erasureRecordId, archiveObjectsDeleted, subjectIds.Count, now, cancellationToken);
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
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await using var failureConnection = await dataSource.OpenConnectionAsync(cancellationToken);
            await ErasureRecordQuery.FailSiteErasureAsync(
                failureConnection, erasureRecordId, ex.GetType().Name, clock.UtcNow, cancellationToken);
            throw;
        }
    }

    /// <summary>
    /// `22-30`/`adr/0149` rule 2: "a lifecycle operation completes when the module proves it, never
    /// when chat has sent it." Reads every module this site has ever had -
    /// <see cref="IEnabledModuleReadStore.GetAllForSiteAsync"/>'s own unfiltered read, which after this
    /// item includes a revoked or lapsed grant exactly as much as an active one, because neither case
    /// deletes the row any more - and asks each one, over the deployment-wide provisioning secret (not
    /// a per-site <see cref="ModuleCredential"/>: <see cref="IModuleRegistrationGateway.EraseTenantDataAsync"/>'s
    /// own remarks explain why that is the channel that still reaches a tenant whose per-site
    /// credential is gone), to erase this tenant's data and prove nothing remains.
    ///
    /// <para><b>Why this runs before the archive read and the site delete, not after.</b> The site row
    /// (and, cascading with it, <c>enabled_modules</c> - <c>EnabledModuleConfiguration</c>'s own
    /// <c>HasOne&lt;Site&gt;()</c>) is this deployment's only record of which modules a tenant ever had
    /// and where to reach them. The moment it is deleted, a module that has not yet confirmed can never
    /// be asked again and can never even be *named* in a later failure - `22-30`'s own backlog states
    /// this as the load-bearing ordering, the same class of reasoning <see cref="ProcessSiteAsync"/>'s
    /// own remarks already give for reading archive keys and Keycloak subject ids before the site
    /// row goes.</para>
    ///
    /// <para><b>A module that does not confirm never auto-completes.</b> Genuinely unreachable
    /// (<see cref="ModuleUnreachableException"/>) and reachable-but-not-yet-confirmed
    /// (<see cref="TenantDataErasureResult.Confirmed"/> <see langword="false"/>) are treated
    /// identically: this tick returns <see langword="false"/> and the site stays flagged for the next
    /// one, the same "not an error and not a stall" shape the conversation gate just above already
    /// uses. Only once <paramref name="requestedAt"/> is more than
    /// <see cref="SiteErasureJobOptions.ModuleUnreachableWindow"/> in the past does this method also
    /// mark the <c>erasure_records</c> receipt <c>Failed</c>, naming the module - a bound stated as an
    /// implementer's-call safety rail, not a measurement (`CLAUDE.md`: "do not invent numbers... a
    /// typical production figure" - the identical posture <c>EnableModuleForSiteAsOwnerHandler.MaxGrantDuration</c>'s
    /// own remarks already take for an unmeasured bound). <see cref="ErasureRecordStatus.Failed"/> is
    /// not terminal here either (that type's own remarks): the very next cycle that finds every module
    /// confirmed moves the record straight to <c>Completed</c>, the same way a conversation-drain
    /// failure already recovers.</para>
    /// </summary>
    /// <returns><see langword="true"/> only once every module this site has ever had confirms nothing
    /// remains.</returns>
    private async Task<bool> EraseModulesAsync(
        Guid siteId, Guid? erasureRecordId, DateTimeOffset requestedAt, DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var moduleReadStore = scope.ServiceProvider.GetRequiredService<IEnabledModuleReadStore>();
        var registrationGateway = scope.ServiceProvider.GetRequiredService<IModuleRegistrationGateway>();

        var modules = await moduleReadStore.GetAllForSiteAsync(new SiteId(siteId), now, cancellationToken);
        if (modules.Count == 0)
        {
            // The ordinary case: a site with no module ever enabled has nothing for this gate to do,
            // unchanged from before this item.
            return true;
        }

        ModuleKey? unconfirmedModule = null;
        var provisioningSecret = provisioningSecrets.TryGet();

        if (provisioningSecret is null)
        {
            // This deployment has never configured a module-provisioning secret at all - every module
            // is unreachable by construction, not by any one module's own fault. Named as its own
            // reason rather than a module key, since no call was ever attempted.
            logger.LogWarning(
                "Site {SiteId} has {ModuleCount} module(s) enabled but this deployment has no module " +
                "provisioning secret configured; erasure cannot reach any of them yet.", siteId, modules.Count);
        }
        else
        {
            foreach (var module in modules)
            {
                try
                {
                    var target = new ModuleRegistrationTarget(module.ModuleKey, new SiteId(siteId), module.EntryPoint);
                    var result = await registrationGateway.EraseTenantDataAsync(
                        target, provisioningSecret.Value, cancellationToken);
                    if (!result.Confirmed)
                    {
                        unconfirmedModule ??= module.ModuleKey;
                    }
                }
                catch (ModuleUnreachableException)
                {
                    unconfirmedModule ??= module.ModuleKey;
                }
            }
        }

        if (unconfirmedModule is null && provisioningSecret is not null)
        {
            return true;
        }

        var staleness = now - requestedAt;
        if (staleness > options.Value.ModuleUnreachableWindow)
        {
            var reason = provisioningSecret is null
                ? "ModuleUnreachable:no-provisioning-secret-configured"
                : $"ModuleUnreachable:{unconfirmedModule!.Value.Value}";

            await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
            await ErasureRecordQuery.FailSiteErasureAsync(connection, erasureRecordId, reason, now, cancellationToken);

            logger.LogWarning(
                "Site {SiteId} erasure has been waiting on a module for {Staleness} (past the " +
                "configured {Window} window); marked Failed. {Reason}",
                siteId, staleness, options.Value.ModuleUnreachableWindow, reason);
        }

        return false;
    }
}
