using Ago.Chat.Contracts;
using Ago.Platform.Kernel;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Ago.Chat.Worker;

/// <summary>
/// `24-12`: `access_records` accumulates one row per boundary-crossing access forever unless something
/// prunes it - this job is that something, per this item's own Done-when ("a stated retention enforced
/// by something that runs"). Same shape as <see cref="WebhookDeliveryPruneJob"/>, deliberately - both
/// are "bounded-batch delete past a configurable window, on a schedule."
/// </summary>
public sealed class AccessRecordPruneJob(
    NpgsqlDataSource dataSource,
    IClock clock,
    IOptions<AccessRecordPruneJobOptions> options,
    ILogger<AccessRecordPruneJob> logger) : BackgroundService
{
    private const string TableTag = "access_records";

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
                logger.LogError(ex, "Access record prune cycle failed; retrying next cycle.");
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
            var removed = await AccessRecordPruneQuery.DeleteOlderThanBatchAsync(
                connection, olderThan, options.Value.BatchSize, cancellationToken);
            totalRemoved += removed;

            if (removed < options.Value.BatchSize)
            {
                break;
            }
        }

        if (totalRemoved > 0)
        {
            logger.LogInformation("Access record prune removed {Count} row(s) older than {OlderThan}.", totalRemoved, olderThan);
        }

        ChatMetrics.RecordRetentionPruneCycle(TableTag, totalRemoved, clock.UtcNow - startedAt);
    }
}
