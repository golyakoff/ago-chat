using Ago.Chat.Contracts;
using Ago.Platform.Kernel;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Ago.Chat.Worker;

/// <summary>
/// `15-04`: outbox rows are never deleted after publication today (`2-01`'s own out-of-scope note,
/// pointed here since 2026-08-24) - this job is that deferred maintenance concern, finally built.
/// Same <c>BackgroundService</c>/<c>PeriodicTimer</c> shape as <see cref="PartitionMaintenanceJob"/>
/// and <see cref="AttachmentOrphanSweepJob"/> (`concurrency.md`, and `15-04`'s own instruction: "reuse
/// that shape, don't invent a second one"). Each cycle issues repeated bounded-batch deletes
/// (<see cref="OutboxPruneQuery"/>) until either nothing is left to prune or
/// <see cref="OutboxPruneJobOptions.MaxBatchesPerCycle"/> is reached, so a single cycle's total work is
/// bounded on both axes - per-statement (<c>BatchSize</c>) and per-cycle (<c>MaxBatchesPerCycle</c>) -
/// while still converging on a real backlog within one cycle in the common case.
/// </summary>
public sealed class OutboxPruneJob(
    NpgsqlDataSource dataSource,
    IClock clock,
    IOptions<OutboxPruneJobOptions> options,
    ILogger<OutboxPruneJob> logger) : BackgroundService
{
    private const string TableTag = "outbox";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(options.Value.Interval);
        do
        {
            try
            {
                await PruneAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // concurrency.md: a BackgroundService catches and continues - a transient Postgres
                // blip here must not permanently kill the prune loop, and must not increment the
                // heartbeat counter either (a cycle that threw did not complete).
                logger.LogError(ex, "Outbox prune cycle failed; retrying next cycle.");
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken)); // runs once immediately, then every Interval
    }

    internal async Task PruneAsync(CancellationToken cancellationToken)
    {
        var startedAt = clock.UtcNow;
        var olderThan = startedAt - options.Value.RetentionWindow;

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);

        var totalRemoved = 0;
        for (var batch = 0; batch < options.Value.MaxBatchesPerCycle; batch++)
        {
            var removed = await OutboxPruneQuery.DeletePublishedBatchAsync(
                connection, olderThan, options.Value.BatchSize, cancellationToken);
            totalRemoved += removed;

            if (removed < options.Value.BatchSize)
            {
                // Fewer rows than requested means this was the last batch - caught up, no point
                // issuing another statement that would return zero.
                break;
            }
        }

        if (totalRemoved > 0)
        {
            logger.LogInformation("Outbox prune removed {Count} published row(s) older than {OlderThan}.", totalRemoved, olderThan);
        }

        ChatMetrics.RecordRetentionPruneCycle(TableTag, totalRemoved, clock.UtcNow - startedAt);
    }
}
