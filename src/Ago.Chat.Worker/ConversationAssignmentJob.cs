using Ago.Chat.Application.Abstractions;
using Ago.Chat.Domain;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Ago.Chat.Worker;

/// <summary>
/// `4-02`/`4-03`: the tick loop shared by both assignment mechanisms - `concurrency.md`'s "Operator
/// assignment - the contended path". Multiple `Worker` replicas run this at once and never conflict
/// on purpose; which mechanism actually performs the claim (`4-02`'s `SKIP LOCKED`, or `4-03`'s
/// per-operator Redis lock) is entirely behind <see cref="IAssignmentClaimer"/>, chosen once at
/// startup by config (`Program.cs`) - this class knows nothing about either implementation.
/// </summary>
public sealed class ConversationAssignmentJob(
    NpgsqlDataSource dataSource,
    IAssignmentClaimer claimer,
    IOptions<ConversationAssignmentJobOptions> options,
    ILogger<ConversationAssignmentJob> logger) : BackgroundService
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
                // concurrency.md: a BackgroundService catches and continues - a transient Postgres
                // blip here must not permanently kill the assignment loop.
                logger.LogError(ex, "Assignment cycle failed; retrying next cycle.");
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken)); // runs once immediately, then every Interval
    }

    internal async Task RunOnceAsync(CancellationToken cancellationToken)
    {
        foreach (var siteId in await GetSiteIdsWithWaitingConversationsAsync(cancellationToken))
        {
            try
            {
                await claimer.AssignWaitingConversationsAsync(siteId, options.Value.BatchSize, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // A claimer's own internal contention (e.g. SkipLockedAssignmentClaimer's
                // transaction-level Postgres deadlock, SqlState 40P01, when a batch assigning
                // several operators races another replica's batch touching them in a different
                // order) is exactly as normal as a single claim losing its race -
                // concurrency.md's "not an error to log at Error level" extended to the whole
                // attempt. One site's contention must not stall every other site this tick.
                logger.LogDebug(ex, "Assignment batch for site {SiteId} failed this tick; retrying next tick.", siteId);
            }
        }
    }

    private async Task<IReadOnlyList<SiteId>> GetSiteIdsWithWaitingConversationsAsync(CancellationToken cancellationToken)
    {
        const string sql = "SELECT DISTINCT site_id FROM conversations WHERE state = 'Waiting'";

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
