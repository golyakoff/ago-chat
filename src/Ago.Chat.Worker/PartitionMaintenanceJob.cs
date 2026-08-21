using Ago.Platform.Kernel;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Ago.Chat.Worker;

/// <summary>
/// data-model.md's Partitioning section: keeps the current month plus the next
/// <see cref="PartitionMaintenanceJobOptions.MonthsAhead"/> months of <c>messages</c> partitions
/// always present, so a row is never rejected for landing in a month with no partition yet
/// (Stage2PartitionMessages creates the first three; this job is what keeps that true after
/// deploy day). Raw Npgsql, not EF - <c>CREATE TABLE ... PARTITION OF</c> has no EF Core fluent-API
/// shape at all, matching <see cref="OutboxDispatcher"/>'s precedent for imperative DDL/SQL work in
/// this host. <c>CREATE TABLE IF NOT EXISTS</c> per partition is the whole idempotency story: safe
/// under a missed run (next run's IF NOT EXISTS is a no-op for partitions that already exist) and
/// under two Worker replicas racing to create the same partition (concurrency.md; one wins, the
/// other's IF NOT EXISTS is a no-op too) - exactly the "many workers create the same thing, first
/// one wins, nobody cares" pattern IF NOT EXISTS exists for.
/// </summary>
public sealed class PartitionMaintenanceJob(
    NpgsqlDataSource dataSource,
    IClock clock,
    IOptions<PartitionMaintenanceJobOptions> options,
    ILogger<PartitionMaintenanceJob> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(options.Value.Interval);
        do
        {
            try
            {
                await EnsurePartitionsAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // concurrency.md: a BackgroundService catches and continues - a transient Postgres
                // blip here must not permanently kill the loop that keeps partitions ahead of need.
                logger.LogError(ex, "Partition maintenance cycle failed; retrying next cycle.");
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken)); // runs once immediately, then every Interval
    }

    internal async Task EnsurePartitionsAsync(CancellationToken cancellationToken)
    {
        var now = clock.UtcNow;
        var monthStart = new DateTimeOffset(now.Year, now.Month, 1, 0, 0, 0, TimeSpan.Zero);

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        for (var i = 0; i <= options.Value.MonthsAhead; i++)
        {
            var from = monthStart.AddMonths(i);
            var to = monthStart.AddMonths(i + 1);
            var partitionName = $"messages_{from:yyyy_MM}";

            var sql = $"""
                CREATE TABLE IF NOT EXISTS {partitionName} PARTITION OF messages
                    FOR VALUES FROM ('{from:yyyy-MM-dd}') TO ('{to:yyyy-MM-dd}');
                """;
            await using var command = new NpgsqlCommand(sql, connection);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
    }
}
