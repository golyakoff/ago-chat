using Ago.Chat.Domain;
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
///
/// <para><b>`13-06`/`adr/0031`: one more dimension, not a different shape.</b> `messages` is now
/// <c>PARTITION BY LIST (retention_class)</c> at the top, each class itself <c>PARTITION BY RANGE
/// (created_at)</c> monthly - this job now ensures both levels: the (fixed, small -
/// <see cref="RetentionClass.KnownClasses"/>) set of class-level partitions, idempotently, and then
/// the same current-month-plus-<see cref="PartitionMaintenanceJobOptions.MonthsAhead"/> monthly grid
/// underneath *each* one. The class-level `CREATE TABLE IF NOT EXISTS` is here rather than only in
/// the migration for the same reason the monthly one always was: a class-level partition is
/// deployment-time DDL a fresh environment or a `KnownClasses` addition should not depend on a
/// migration having run first, matching this job's own established "the migration seeds it, this job
/// keeps it true forever after" split for the monthly grid.</para>
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

        foreach (var retentionClass in RetentionClass.KnownClasses)
        {
            var classPartitionName = MessagePartitionNames.ForClass(retentionClass);
            var classSql = $"""
                CREATE TABLE IF NOT EXISTS {classPartitionName} PARTITION OF messages
                    FOR VALUES IN ('{retentionClass.Value}') PARTITION BY RANGE (created_at);
                """;
            await using (var classCommand = new NpgsqlCommand(classSql, connection))
            {
                await classCommand.ExecuteNonQueryAsync(cancellationToken);
            }

            for (var i = 0; i <= options.Value.MonthsAhead; i++)
            {
                var from = monthStart.AddMonths(i);
                var to = monthStart.AddMonths(i + 1);
                var partitionName = MessagePartitionNames.ForMonth(retentionClass, from);

                var sql = $"""
                    CREATE TABLE IF NOT EXISTS {partitionName} PARTITION OF {classPartitionName}
                        FOR VALUES FROM ('{from:yyyy-MM-dd}') TO ('{to:yyyy-MM-dd}');
                    """;
                await using var command = new NpgsqlCommand(sql, connection);
                await command.ExecuteNonQueryAsync(cancellationToken);
            }
        }
    }
}
