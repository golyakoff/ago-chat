using Ago.Chat.Contracts;
using Ago.Platform.Kernel;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Ago.Chat.Worker;

/// <summary>
/// `15-04`: `webhook_deliveries` accumulates one row per delivery attempt-summary forever today
/// (`6-03`'s own Goal) - this job is the deferred maintenance concern `15-04`'s Scope names for it.
/// Same shape as <see cref="OutboxPruneJob"/>, deliberately - both are "bounded-batch delete past a
/// configurable window, on a schedule" and nothing about either table's own semantics changes that.
/// </summary>
public sealed class WebhookDeliveryPruneJob(
    NpgsqlDataSource dataSource,
    IClock clock,
    IOptions<WebhookDeliveryPruneJobOptions> options,
    ILogger<WebhookDeliveryPruneJob> logger) : BackgroundService
{
    private const string TableTag = "webhook_deliveries";

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
                logger.LogError(ex, "Webhook delivery prune cycle failed; retrying next cycle.");
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
            var removed = await WebhookDeliveryPruneQuery.DeleteOlderThanBatchAsync(
                connection, olderThan, options.Value.BatchSize, cancellationToken);
            totalRemoved += removed;

            if (removed < options.Value.BatchSize)
            {
                break;
            }
        }

        if (totalRemoved > 0)
        {
            logger.LogInformation("Webhook delivery prune removed {Count} row(s) older than {OlderThan}.", totalRemoved, olderThan);
        }

        ChatMetrics.RecordRetentionPruneCycle(TableTag, totalRemoved, clock.UtcNow - startedAt);
    }
}
