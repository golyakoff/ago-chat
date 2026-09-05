using Ago.Chat.Contracts;
using Ago.Platform.Kernel;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Ago.Chat.Worker;

/// <summary>
/// `23-19`: <c>channel_deliveries</c> accumulates one row per outbound channel send forever otherwise -
/// this job is the item's own scope note ("Retention: its own window and its own prune job"). Same
/// shape as <see cref="WebhookDeliveryPruneJob"/>, deliberately - both are "bounded-batch delete past a
/// configurable window, on a schedule" and nothing about either table's own semantics changes that.
/// </summary>
public sealed class ChannelDeliveryPruneJob(
    NpgsqlDataSource dataSource,
    IClock clock,
    IOptions<ChannelDeliveryPruneJobOptions> options,
    ILogger<ChannelDeliveryPruneJob> logger) : BackgroundService
{
    private const string TableTag = "channel_deliveries";

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
                logger.LogError(ex, "Channel delivery prune cycle failed; retrying next cycle.");
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
            var removed = await ChannelDeliveryPruneQuery.DeleteOlderThanBatchAsync(
                connection, olderThan, options.Value.BatchSize, cancellationToken);
            totalRemoved += removed;

            if (removed < options.Value.BatchSize)
            {
                break;
            }
        }

        if (totalRemoved > 0)
        {
            logger.LogInformation("Channel delivery prune removed {Count} row(s) older than {OlderThan}.", totalRemoved, olderThan);
        }

        ChatMetrics.RecordRetentionPruneCycle(TableTag, totalRemoved, clock.UtcNow - startedAt);
    }
}
