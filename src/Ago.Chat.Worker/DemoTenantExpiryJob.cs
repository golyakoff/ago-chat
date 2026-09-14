using Ago.Chat.Application.Abstractions;
using Ago.Platform.Abstractions;
using Ago.Platform.Kernel;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Ago.Chat.Worker;

/// <summary>
/// `8-07`/`adr/0058`: deletes demo tenants whose window has passed, and everything under them.
///
/// <para><b>This is the narrow erasure `8-07` needed and `16-02` does not yet provide.</b> `16-02` is
/// scoped and unbuilt, so "reuse `16-02`'s deletion" was not available; rather than tick the item's
/// Done-when on a job that removes a Keycloak user and leaves rows behind, this does the whole removal
/// for the one scope it needs - a site and its subtree - in the shape `16-02` can generalise: a
/// bounded-batch Worker job, ordered object-store-then-database-then-identity-provider, with the reach
/// stated rather than assumed. `adr/0058` records exactly what it does and does not touch.</para>
///
/// <para>Same <see cref="PeriodicTimer"/>/<see cref="BackgroundService"/> shape as
/// <see cref="AttachmentOrphanSweepJob"/> and `4-04`'s disconnect sweep: once immediately, then every
/// interval, and a transient failure logs and retries next tick rather than killing the backstop
/// (`concurrency.md`). Retrying is safe because every step is idempotent - a site row already gone
/// deletes zero rows, a storage object already gone is a no-op (`5-02`), and a Keycloak user already
/// gone is a success by this port's contract.</para>
///
/// <para><b>`25-82`: <see cref="IServiceScopeFactory"/> is new here</b>, for exactly one call in
/// <see cref="RemoveAsync"/> - resolving <see cref="ISiteErasurePublisher"/> fresh, once per tenant
/// removed, because its implementation holds an <c>AgoChatDbContext</c> that must never be captured for
/// this job's own singleton lifetime (that port's own remarks explain why). Every other dependency here
/// is unchanged and stays directly injected, because none of them carry that constraint.</para>
/// </summary>
public sealed class DemoTenantExpiryJob(
    IDemoTenantRepository demoTenants,
    IDemoIdentityProvisioner identities,
    IFileStorage fileStorage,
    IServiceScopeFactory scopeFactory,
    IClock clock,
    IOptions<DemoTenantExpiryJobOptions> options,
    ILogger<DemoTenantExpiryJob> logger) : BackgroundService
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
                logger.LogError(ex, "Demo tenant expiry cycle failed; retrying next cycle.");
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    /// <summary>
    /// One bounded pass. <c>internal</c> so an integration test can drive exactly one cycle against a
    /// real Postgres and a real Keycloak instead of waiting for a timer - the same seam
    /// <see cref="AttachmentOrphanSweepJob.SweepAsync"/> already exposes for the same reason.
    /// </summary>
    internal async Task<int> SweepAsync(CancellationToken cancellationToken)
    {
        var now = clock.UtcNow;
        var expired = await demoTenants.ListExpiredAsync(now, options.Value.BatchSize, cancellationToken);
        var removed = 0;

        foreach (var tenant in expired)
        {
            try
            {
                await RemoveAsync(tenant, cancellationToken);
                removed++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // One tenant's failure must not stop the others: a single unreachable object or a
                // Keycloak hiccup would otherwise hold up every expiry behind it, and this is the job
                // whose whole purpose is that nothing is held up.
                logger.LogError(
                    ex, "Failed to remove expired demo tenant {PublicKey}; it stays for the next cycle.",
                    tenant.PublicKey);
            }
        }

        if (removed > 0)
        {
            logger.LogInformation("Removed {Removed} expired demo tenant(s).", removed);
        }

        return removed;
    }

    /// <summary>
    /// <b>The order is the design.</b> Object store, then Postgres (now, `25-82`, together with the
    /// <see cref="Ago.Chat.Contracts.SiteErased"/> outbox row), then Keycloak - and each step is
    /// chosen so an interruption leaves something a later cycle can still finish:
    /// <list type="bullet">
    /// <item><b>Objects first</b>, because the rows that name them are about to be deleted. After the
    /// site row is gone there is nothing left to enumerate, and the bytes would sit in MinIO forever -
    /// which is the gap `personal-data.md` already records for conversation deletion, and the one thing
    /// this job must not reproduce.</item>
    /// <item><b>Postgres second.</b> Once it commits, the tenant is gone from every read path in the
    /// product, so a viewer can no longer reach anything even if the last step has not run - and, as of
    /// `25-82`, every other product holding a copy of something about this tenant now has a fact to
    /// eventually learn from too, staged in the identical commit (rule 4).</item>
    /// <item><b>Keycloak last</b>, because it is the step most likely to fail (a network hop to another
    /// process) and the least harmful to leave undone for a cycle: a user whose site no longer exists
    /// can log in and see nothing at all - `ResolveOperatorIdentityHandler` finds no operator row.
    /// Crucially, the sweeper still finds it next cycle: `ListExpiredAsync` reads the *site*, so
    /// leaving Keycloak behind means leaving Postgres behind too, and the pair are retried
    /// together.</item>
    /// </list>
    /// The subject ids are read before the delete precisely so the last step is still possible after
    /// the second one has removed the rows holding them.
    /// </summary>
    private async Task RemoveAsync(ExpiredDemoTenant tenant, CancellationToken cancellationToken)
    {
        var objectKeys = await demoTenants.ListAttachmentObjectKeysAsync(tenant.SiteId, cancellationToken);
        foreach (var key in objectKeys)
        {
            await fileStorage.DeleteAsync(new ObjectKey(key), cancellationToken);
        }

        // `25-82`: a fresh scope for this one call, never captured beyond it - ISiteErasurePublisher's
        // own remarks explain why its AgoChatDbContext must not live as long as this job does. The
        // returned bool (false only when the site was already gone - a retry after a previous crash, or
        // a racing replica) is not branched on here: identity cleanup below has always run regardless of
        // whether the site row was already gone, and that is unchanged.
        await using (var scope = scopeFactory.CreateAsyncScope())
        {
            var erasure = scope.ServiceProvider.GetRequiredService<ISiteErasurePublisher>();
            await erasure.EraseAndPublishAsync(tenant.SiteId, clock.UtcNow, cancellationToken);
        }

        foreach (var subjectId in tenant.ExternalSubjectIds)
        {
            await identities.DeleteAsync(subjectId, cancellationToken);
        }

        logger.LogInformation(
            "Expired demo tenant {PublicKey} removed: {ObjectCount} storage object(s), the site subtree, "
            + "and {IdentityCount} identity-provider user(s).",
            tenant.PublicKey, objectKeys.Count, tenant.ExternalSubjectIds.Count);
    }
}
