using Ago.Chat.Contracts;
using Ago.Platform.Kernel;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Ago.Chat.Worker;

/// <summary>
/// `23-11`'s own Scope: "a reveal record... its own retention." `contact_reveals` accumulates one row
/// per deliberate unmasking forever unless something prunes it - this job is that something, the same
/// "bounded-batch delete past a configurable window, on a schedule" shape
/// <see cref="AccessRecordPruneJob"/>/<see cref="WebhookDeliveryPruneJob"/> already establish.
/// </summary>
public sealed class ContactRevealPruneJob(
    NpgsqlDataSource dataSource,
    IClock clock,
    IOptions<ContactRevealPruneJobOptions> options,
    ILogger<ContactRevealPruneJob> logger) : BackgroundService
{
    private const string TableTag = "contact_reveals";

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
                logger.LogError(ex, "Contact reveal prune cycle failed; retrying next cycle.");
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
            var removed = await ContactRevealPruneQuery.DeleteOlderThanBatchAsync(
                connection, olderThan, options.Value.BatchSize, cancellationToken);
            totalRemoved += removed;

            if (removed < options.Value.BatchSize)
            {
                break;
            }
        }

        if (totalRemoved > 0)
        {
            logger.LogInformation("Contact reveal prune removed {Count} row(s) older than {OlderThan}.", totalRemoved, olderThan);
        }

        ChatMetrics.RecordRetentionPruneCycle(TableTag, totalRemoved, clock.UtcNow - startedAt);
    }
}
