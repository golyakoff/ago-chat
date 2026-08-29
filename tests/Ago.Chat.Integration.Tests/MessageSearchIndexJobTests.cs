using Ago.Chat.Worker;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Ago.Chat.Integration.Tests;

/// <summary>
/// `18-01`: real Postgres, a real `CREATE INDEX CONCURRENTLY` actually run against a real partition -
/// not asserted by inspecting the SQL string. Every partition this suite creates is far in the past
/// (year 2003) so it can never collide with <see cref="PartitionMaintenanceJob"/>'s own ongoing
/// current-month activity in this shared fixture, matching <c>MessagePartitionPruneJobTests</c>'s own
/// convention (which reserves 1999-2001) with its own distinct year.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class MessageSearchIndexJobTests(PostgresFixture fixture)
{
    [Fact]
    public async Task EnsureIndexesAsync_CreatesBothIndexes_ForAnExistingPartition()
    {
        var partitionName = await CreatePartitionAsync(2003, 1);
        try
        {
            var job = CreateJob();

            await job.EnsureIndexesAsync(CancellationToken.None);

            Assert.True(await IndexExistsAsync($"ix_{partitionName}_site_created"));
            Assert.True(await IndexExistsAsync($"ix_{partitionName}_search"));
        }
        finally
        {
            await DropIfExistsAsync(partitionName);
        }
    }

    [Fact]
    public async Task EnsureIndexesAsync_RunTwice_IsIdempotent_AndNeverErrors()
    {
        var partitionName = await CreatePartitionAsync(2003, 2);
        try
        {
            var job = CreateJob();

            await job.EnsureIndexesAsync(CancellationToken.None);
            await job.EnsureIndexesAsync(CancellationToken.None);

            Assert.Equal(1, await IndexCountAsync($"ix_{partitionName}_site_created"));
            Assert.Equal(1, await IndexCountAsync($"ix_{partitionName}_search"));
        }
        finally
        {
            await DropIfExistsAsync(partitionName);
        }
    }

    /// <summary>A partition `PartitionMaintenanceJob` creates *after* this job's first cycle still
    /// gets indexed on the next one - proves the job re-enumerates the live catalog every cycle
    /// rather than remembering a partition list from its first run.</summary>
    [Fact]
    public async Task EnsureIndexesAsync_IndexesAPartitionCreatedAfterTheFirstCycle()
    {
        var firstPartition = await CreatePartitionAsync(2003, 3);
        string? secondPartition = null;
        try
        {
            var job = CreateJob();
            await job.EnsureIndexesAsync(CancellationToken.None);
            Assert.True(await IndexExistsAsync($"ix_{firstPartition}_search"));

            secondPartition = await CreatePartitionAsync(2003, 4);
            await job.EnsureIndexesAsync(CancellationToken.None);

            Assert.True(await IndexExistsAsync($"ix_{secondPartition}_search"));
        }
        finally
        {
            await DropIfExistsAsync(firstPartition);
            if (secondPartition is not null)
            {
                await DropIfExistsAsync(secondPartition);
            }
        }
    }

    private MessageSearchIndexJob CreateJob() =>
        new(fixture.DataSource,
            Options.Create(new MessageSearchIndexJobOptions { Interval = TimeSpan.FromMinutes(10) }),
            NullLogger<MessageSearchIndexJob>.Instance);

    private async Task<string> CreatePartitionAsync(int year, int month)
    {
        var name = $"messages_{year:0000}_{month:00}";
        var from = new DateOnly(year, month, 1);
        var to = from.AddMonths(1);

        await using var connection = await fixture.DataSource.OpenConnectionAsync();
        var sql = $"""
            CREATE TABLE IF NOT EXISTS {name} PARTITION OF messages
                FOR VALUES FROM ('{from:yyyy-MM-dd}') TO ('{to:yyyy-MM-dd}');
            """;
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
        return name;
    }

    private async Task<bool> IndexExistsAsync(string indexName) => await IndexCountAsync(indexName) > 0;

    private async Task<int> IndexCountAsync(string indexName)
    {
        await using var connection = await fixture.DataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand("SELECT count(*) FROM pg_class WHERE relname = @name AND relkind = 'i'", connection);
        command.Parameters.AddWithValue("name", indexName);
        return (int)(long)(await command.ExecuteScalarAsync())!;
    }

    private async Task DropIfExistsAsync(string partitionName)
    {
        await using var connection = await fixture.DataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand($"DROP TABLE IF EXISTS {partitionName};", connection);
        await command.ExecuteNonQueryAsync();
    }
}
