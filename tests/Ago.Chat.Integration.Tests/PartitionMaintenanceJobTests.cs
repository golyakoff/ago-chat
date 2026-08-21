using Ago.Chat.Worker;
using Ago.Platform.Kernel;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Ago.Chat.Integration.Tests;

/// <summary>2-06's backlog item: run against a database missing a future month's partition, assert
/// it gets created; run again immediately, assert no error and no duplicate partition. Each test
/// starts/stops its own <see cref="PartitionMaintenanceJob"/> via the public
/// <see cref="BackgroundService"/> API - the same lifecycle a real host drives, matching
/// <see cref="OutboxDispatcherTests"/>' own convention - rather than reaching for its internal
/// method, which is internal for the same reason <c>OutboxDispatcher.DispatchBatchAsync</c> is: the
/// public start/stop lifecycle is what a real deployment actually exercises.
/// <see cref="FixedClock"/> is set well past the three months Stage2PartitionMessages creates at
/// migration time, so the target partition is guaranteed absent going in - not relying on wall-clock
/// timing relative to whenever this test happens to run.</summary>
[Collection(PostgresCollection.Name)]
public sealed class PartitionMaintenanceJobTests(PostgresFixture fixture)
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task StartingTheJob_WhenAFuturePartitionIsMissing_CreatesIt()
    {
        var farFuture = DateTimeOffset.UtcNow.AddMonths(6);
        var partitionName = $"messages_{farFuture:yyyy_MM}";
        Assert.False(await PartitionExistsAsync(partitionName));

        var job = CreateJob(farFuture);
        await job.StartAsync(CancellationToken.None);
        try
        {
            Assert.True(await OutboxTestHelpers.WaitUntilAsync(() => PartitionExistsAsync(partitionName), Timeout));
        }
        finally
        {
            await job.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task TwoJobsRacingForTheSameMonth_NeitherErrors_AndThePartitionExistsExactlyOnce()
    {
        var farFuture = DateTimeOffset.UtcNow.AddMonths(7);
        var partitionName = $"messages_{farFuture:yyyy_MM}";

        var jobA = CreateJob(farFuture);
        var jobB = CreateJob(farFuture);
        await jobA.StartAsync(CancellationToken.None);
        await jobB.StartAsync(CancellationToken.None); // overlapping run, concurrency.md's "many Worker replicas" scenario
        try
        {
            Assert.True(await OutboxTestHelpers.WaitUntilAsync(() => PartitionExistsAsync(partitionName), Timeout));
        }
        finally
        {
            await jobA.StopAsync(CancellationToken.None);
            await jobB.StopAsync(CancellationToken.None);
        }

        Assert.Equal(1, await PartitionRowCountAsync(partitionName));
    }

    [Fact]
    public async Task StartingTheJob_CreatesTheCurrentMonthAndTheConfiguredNumberAhead_ButNoFurther()
    {
        var reference = DateTimeOffset.UtcNow.AddMonths(8);
        var lastExpected = $"messages_{reference.AddMonths(2):yyyy_MM}";
        var tooFar = $"messages_{reference.AddMonths(3):yyyy_MM}";

        var job = CreateJob(reference, monthsAhead: 2);
        await job.StartAsync(CancellationToken.None);
        try
        {
            Assert.True(await OutboxTestHelpers.WaitUntilAsync(() => PartitionExistsAsync(lastExpected), Timeout));
        }
        finally
        {
            await job.StopAsync(CancellationToken.None);
        }

        Assert.False(await PartitionExistsAsync(tooFar));
    }

    private PartitionMaintenanceJob CreateJob(DateTimeOffset now, int monthsAhead = 2) =>
        new(fixture.DataSource, new FixedClock(now),
            // Interval is irrelevant to these assertions - ExecuteAsync's do/while runs once
            // immediately on start, before ever waiting on the timer.
            Options.Create(new PartitionMaintenanceJobOptions { MonthsAhead = monthsAhead, Interval = TimeSpan.FromMinutes(10) }),
            NullLogger<PartitionMaintenanceJob>.Instance);

    private async Task<bool> PartitionExistsAsync(string partitionName) => await PartitionRowCountAsync(partitionName) > 0;

    private async Task<int> PartitionRowCountAsync(string partitionName)
    {
        await using var connection = await fixture.DataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand("SELECT count(*) FROM pg_class WHERE relname = @name", connection);
        command.Parameters.AddWithValue("name", partitionName);
        return (int)(long)(await command.ExecuteScalarAsync())!;
    }

    private sealed class FixedClock(DateTimeOffset now) : IClock
    {
        public DateTimeOffset UtcNow => now;
    }
}
