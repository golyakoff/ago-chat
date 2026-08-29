using Microsoft.Extensions.Options;
using Npgsql;

namespace Ago.Chat.Worker;

/// <summary>
/// `18-01`: builds and maintains the two indexes `18-01`'s search depends on - a composite
/// `(site_id, created_at)` and a full-text GIN on `to_tsvector('simple', body)` - once per leaf
/// `messages` partition. Per-partition raw SQL, not EF, matching every other partition-DDL job in this
/// file's neighbourhood (`PartitionMaintenanceJob`'s own remarks: "`CREATE TABLE ... PARTITION OF` has
/// no EF Core fluent-API shape at all" - the same is true of a functional GIN expression index).
///
/// <para><b>Why this lives in a background job and not `18-01`'s own EF migration.</b>
/// `CREATE INDEX CONCURRENTLY` is the only way to add either index to a table this size without
/// holding a lock that blocks every write to that partition for the whole build - and Postgres
/// refuses to run it inside any transaction, which rules out both an EF migration's own wrapping
/// transaction and a `DO $$ ... $$` block (`CONCURRENTLY` is refused inside a function body too, not
/// just an explicit `BEGIN`). Separately, which leaf partitions exist right now is a live catalog
/// fact - `PartitionMaintenanceJob` keeps creating new ones on its own schedule - and
/// `Migration.Up(MigrationBuilder)` has no database connection of its own to ask; it only builds a
/// list of operations for the migrator to run later. A migration could still guess a partition list
/// from a clock computation (`Stage2PartitionMessages`'s own technique), but that guess would go
/// stale silently the day the operational partition count drifts from it, and nothing would notice.
/// A running background service has neither problem: a real connection to enumerate `pg_inherits`,
/// and no ambient transaction to fight <c>CONCURRENTLY</c> over.</para>
///
/// <para>Enumerates existing partitions via <see cref="MessagePartitionPruneQuery.ListPartitionsAsync"/> -
/// the same live-catalog read <see cref="MessagePartitionPruneJob"/> already uses. Idempotent per
/// index (a catalog check before every `CREATE INDEX CONCURRENTLY IF NOT EXISTS`) - safe under a
/// missed run and under two Worker replicas racing the same partition: Postgres itself serialises two
/// concurrent `CONCURRENTLY` builds of the same index, so a losing racer's statement simply waits for
/// the winner's build to finish and then finds `IF NOT EXISTS` already satisfied, the same "first one
/// wins, nobody cares" shape <see cref="PartitionMaintenanceJob"/>'s own remarks describe for
/// `CREATE TABLE IF NOT EXISTS`.</para>
/// </summary>
public sealed class MessageSearchIndexJob(
    NpgsqlDataSource dataSource,
    IOptions<MessageSearchIndexJobOptions> options,
    ILogger<MessageSearchIndexJob> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(options.Value.Interval);
        do
        {
            try
            {
                await EnsureIndexesAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Message search index maintenance cycle failed; retrying next cycle.");
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    internal async Task EnsureIndexesAsync(CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        var partitions = await MessagePartitionPruneQuery.ListPartitionsAsync(connection, cancellationToken);

        var created = 0;
        foreach (var partition in partitions)
        {
            created += await CreateIndexIfMissingAsync(
                connection,
                $"ix_{partition.Name}_site_created",
                $"CREATE INDEX CONCURRENTLY IF NOT EXISTS ix_{partition.Name}_site_created ON {partition.Name} (site_id, created_at)",
                cancellationToken);
            created += await CreateIndexIfMissingAsync(
                connection,
                $"ix_{partition.Name}_search",
                $"CREATE INDEX CONCURRENTLY IF NOT EXISTS ix_{partition.Name}_search ON {partition.Name} USING gin (to_tsvector('simple', body))",
                cancellationToken);
        }

        if (created > 0)
        {
            logger.LogInformation("Message search index maintenance created {Count} index(es).", created);
        }
    }

    /// <summary>The catalog check up front, rather than relying on `IF NOT EXISTS` alone, so the
    /// overwhelmingly common "already built" cycle never issues a `CONCURRENTLY` statement at all -
    /// `IF NOT EXISTS` would still be correct on its own, but Postgres logs a `NOTICE` for every skip,
    /// and this avoids that noise for what is, in steady state, every index on every partition, every
    /// cycle, forever.</summary>
    private static async Task<int> CreateIndexIfMissingAsync(
        NpgsqlConnection connection, string indexName, string sql, CancellationToken cancellationToken)
    {
        const string existsSql = "SELECT 1 FROM pg_class WHERE relname = @name";
        await using (var existsCommand = new NpgsqlCommand(existsSql, connection))
        {
            existsCommand.Parameters.AddWithValue("name", indexName);
            if (await existsCommand.ExecuteScalarAsync(cancellationToken) is not null)
            {
                return 0;
            }
        }

        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync(cancellationToken);
        return 1;
    }
}
