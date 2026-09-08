using Ago.Chat.Infrastructure.Postgres.Backfill;
using Ago.Chat.Infrastructure.Postgres.Persistence;
using Ago.Platform.Kernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Ago.Chat.Worker;

/// <summary>
/// `23-59`/`adr/0147`: the automatic trigger <see cref="ContactCarryoverBackfill"/>'s own remarks say
/// it needs - a recurring sweep, the same <c>PeriodicTimer</c>/<c>BackgroundService</c> shape every
/// other retention/erasure job in this file uses, so nobody has to remember to run anything
/// (`23-104`'s own lesson, restated by <see cref="ContactCarryoverBackfill"/>'s own remarks).
///
/// <para>One <see cref="AgoChatDbContext"/> per cycle, built from a fresh connection the same way
/// <c>OperatorConversationReleaser</c> already does for a Worker-side batch that needs EF - not
/// resolved from DI, because this type is not itself scoped and a <c>BackgroundService</c> is a
/// singleton (`ConversationAssignmentIntervalSql`'s own remarks on why the claimers construct their
/// own context rather than injecting one).</para>
/// </summary>
public sealed class ContactCarryoverJob(
    NpgsqlDataSource dataSource,
    IIdGenerator idGenerator,
    IClock clock,
    IOptions<ContactCarryoverJobOptions> options,
    ILogger<ContactCarryoverJob> logger) : BackgroundService
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
                logger.LogError(ex, "Contact carryover cycle failed; retrying next cycle.");
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    /// <summary>One bounded pass over up to <see cref="ContactCarryoverJobOptions.SiteBatchSize"/>
    /// pending sites - <c>internal</c> so an integration test can drive exactly one cycle instead of
    /// waiting for a timer, the same seam every other job in this project exposes for the same
    /// reason.</summary>
    internal async Task<int> SweepAsync(CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        var dbOptions = new DbContextOptionsBuilder<AgoChatDbContext>().UseNpgsql(connection).Options;
        await using var db = new AgoChatDbContext(dbOptions);
        var backfill = new ContactCarryoverBackfill(db, idGenerator, clock);

        var pendingSiteIds = await backfill.ListPendingSiteIdsAsync(options.Value.SiteBatchSize, cancellationToken);

        var published = 0;
        foreach (var siteId in pendingSiteIds)
        {
            try
            {
                var outcome = await backfill.RunOneBatchAsync(siteId, options.Value.ContactBatchSize, cancellationToken);
                published += outcome.Published;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // One site's failure must not stop the others claimed in this cycle - the same
                // reasoning ConversationErasureJob.SweepAsync's own per-item try/catch gives. The
                // site's own request row is untouched by a thrown exception (ContactCarryoverBackfill's
                // own remarks: the whole batch rolls back), so it is picked up again next cycle with no
                // lost progress.
                logger.LogError(
                    ex, "Failed to carry over contacts for site {SiteId}; it stays pending for the next cycle.",
                    siteId.Value);
            }
        }

        if (published > 0)
        {
            logger.LogInformation("Contact carryover staged {Count} ContactCollected event(s) to the outbox.", published);
        }

        return published;
    }
}
