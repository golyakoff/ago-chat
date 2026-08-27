using Ago.Chat.Application.Abstractions;
using Ago.Chat.Contracts;
using Ago.Platform.Kernel;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Ago.Chat.Worker;

/// <summary>
/// `15-04`/`adr/0031`: the drop half of `data-model.md`'s partitioning story - `2-06`'s own out-of-scope
/// note named `DROP` as the cheap-retention mechanism partitioning enables and deferred building it
/// here. Same shape as <see cref="PartitionMaintenanceJob"/> (its own natural counterpart - one creates
/// ahead of need, this one drops past need) but gated where that one is not: every drop candidate is
/// checked against <see cref="IMessageArchiveGate"/> first, per `adr/0031`'s ordering rule ("nothing is
/// dropped until its archive is confirmed written"). `13-06` is not built yet, so the gate resolved here
/// is <see cref="AlwaysConfirmedMessageArchiveGate"/> - see that class's own remarks for why that is
/// honest today rather than a corner cut. When `13-06` replaces it with a real, object-storage-backed
/// implementation, this job's own code does not change at all; only the DI registration in
/// <c>ChatModule</c> does.
/// </summary>
public sealed class MessagePartitionPruneJob(
    NpgsqlDataSource dataSource,
    IMessageArchiveGate archiveGate,
    IClock clock,
    IOptions<MessagePartitionPruneJobOptions> options,
    ILogger<MessagePartitionPruneJob> logger) : BackgroundService
{
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
                logger.LogError(ex, "Message partition prune cycle failed; retrying next cycle.");
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    internal async Task PruneAsync(CancellationToken cancellationToken)
    {
        var startedAt = clock.UtcNow;
        var currentMonthStart = new DateOnly(startedAt.Year, startedAt.Month, 1);
        var cutoff = currentMonthStart.AddMonths(-options.Value.RetentionHorizonMonths);

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        var partitions = await MessagePartitionPruneQuery.ListPartitionsAsync(connection, cancellationToken);

        var dropped = 0;
        var pendingArchive = 0;
        foreach (var partition in partitions)
        {
            if (partition.PeriodEnd > cutoff)
            {
                // Not past the horizon yet - includes, by construction, every partition
                // PartitionMaintenanceJob is actively keeping ready (RetentionHorizonMonths is
                // validated >= 1, so the current month is never a candidate).
                continue;
            }

            var confirmed = await archiveGate.IsArchivedAsync(
                partition.Name, partition.PeriodStart, partition.PeriodEnd, cancellationToken);
            if (!confirmed)
            {
                logger.LogInformation(
                    "Messages partition {Partition} is past its retention horizon but not yet archive-confirmed; leaving it in place.",
                    partition.Name);
                pendingArchive++;
                continue;
            }

            await MessagePartitionPruneQuery.DropPartitionAsync(connection, partition.Name, cancellationToken);
            logger.LogInformation("Dropped messages partition {Partition} (past its {Months}-month retention horizon).",
                partition.Name, options.Value.RetentionHorizonMonths);
            dropped++;
        }

        ChatMetrics.RecordPartitionPruneCycle(dropped, pendingArchive, clock.UtcNow - startedAt);
    }
}
