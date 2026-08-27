using Ago.Chat.Contracts;
using Ago.Platform.Kernel;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Ago.Chat.Worker;

/// <summary>
/// `15-04`: the outbox pattern's consumer-side counterpart - <c>inbox</c> is the idempotency ledger
/// every consumer writes to and nothing ever prunes (`messaging.md`), the same "unbounded, hot,
/// never-visited-by-a-DELETE" shape `outbox` had before <see cref="OutboxPruneJob"/>. See
/// <see cref="InboxPruneJobOptions.RetentionWindow"/> for why 24 hours is enormous headroom above this
/// system's real redelivery window rather than a figure tuned to match it exactly.
/// </summary>
public sealed class InboxPruneJob(
    NpgsqlDataSource dataSource,
    IClock clock,
    IOptions<InboxPruneJobOptions> options,
    ILogger<InboxPruneJob> logger) : BackgroundService
{
    private const string TableTag = "inbox";

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
                logger.LogError(ex, "Inbox prune cycle failed; retrying next cycle.");
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    internal async Task PruneAsync(CancellationToken cancellationToken)
    {
        var startedAt = clock.UtcNow;
        var olderThan = startedAt - options.Value.RetentionWindow;

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);

        var totalRemoved = 0;
        for (var batch = 0; batch < options.Value.MaxBatchesPerCycle; batch++)
        {
            var removed = await InboxPruneQuery.DeleteOlderThanBatchAsync(
                connection, olderThan, options.Value.BatchSize, cancellationToken);
            totalRemoved += removed;

            if (removed < options.Value.BatchSize)
            {
                break;
            }
        }

        if (totalRemoved > 0)
        {
            logger.LogInformation("Inbox prune removed {Count} row(s) older than {OlderThan}.", totalRemoved, olderThan);
        }

        ChatMetrics.RecordRetentionPruneCycle(TableTag, totalRemoved, clock.UtcNow - startedAt);
    }
}
