using Ago.Chat.Contracts;
using Ago.Chat.Worker;
using Ago.Platform.Kernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using OpenTelemetry;
using OpenTelemetry.Metrics;

namespace Ago.Chat.Integration.Tests;

/// <summary>
/// `15-04`: real Postgres (`testing.md`: never mock the database), a real backlog past the retention
/// window - not an empty table (the item's own Done-when). Every assertion is scoped to this test's own
/// seeded ids, never a whole-table count, because `PostgresFixture`'s container is shared with every
/// other test in <see cref="PostgresCollection"/> and nothing resets it between tests
/// (`OutboxDispatcherTests`' own precedent for the same fixture).
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class OutboxPruneJobTests(PostgresFixture fixture)
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan RetentionWindow = TimeSpan.FromHours(24);

    [Fact]
    public async Task PruneAsync_RemovesAPublishedRow_OlderThanTheRetentionWindow()
    {
        var id = await SeedAsync(publishedAt: Now - RetentionWindow - TimeSpan.FromMinutes(1));

        await CreateJob().PruneAsync(CancellationToken.None);

        Assert.False(await OutboxRowExistsAsync(id));
    }

    [Fact]
    public async Task PruneAsync_LeavesAPublishedRow_YoungerThanTheRetentionWindowAlone()
    {
        var id = await SeedAsync(publishedAt: Now - TimeSpan.FromMinutes(1));

        try
        {
            await CreateJob().PruneAsync(CancellationToken.None);

            Assert.True(await OutboxRowExistsAsync(id));
        }
        finally
        {
            await DeleteRowAsync(id);
        }
    }

    /// <summary>`15-04`'s own precondition, never crossed: an unpublished row must never be pruned
    /// regardless of age - only <see cref="Ago.Chat.Worker.OutboxDispatcher"/> is allowed to decide an
    /// outbox row is done, by publishing it. A row this old and still unpublished is exactly the
    /// `OutboxLagGrowing` condition `15-03` alerts on, not something this job should ever make
    /// disappear.</summary>
    [Fact]
    public async Task PruneAsync_NeverRemovesAnUnpublishedRow_RegardlessOfAge()
    {
        var id = await SeedAsync(publishedAt: null, occurredAt: Now - TimeSpan.FromDays(365));

        try
        {
            await CreateJob().PruneAsync(CancellationToken.None);

            Assert.True(await OutboxRowExistsAsync(id));
        }
        finally
        {
            await DeleteRowAsync(id);
        }
    }

    /// <summary>Proves the bounded-batch loop actually converges within one cycle on a backlog larger
    /// than one batch - `15-04`'s own words: "a single unbounded DELETE... is its own incident," which
    /// this test would not distinguish from a correct implementation unless the backlog genuinely
    /// exceeds <c>BatchSize</c>. A distinctive, far-past <c>publishedAt</c> (well outside anything any
    /// other test in this shared fixture ever writes) keeps this test's own rows the only ones eligible
    /// under its own cutoff, regardless of what else the shared table holds.</summary>
    [Fact]
    public async Task PruneAsync_DrainsABacklogLargerThanOneBatch_WithinOneCycle()
    {
        var distantPast = new DateTimeOffset(2000, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var ids = new List<Guid>();
        for (var i = 0; i < 7; i++)
        {
            ids.Add(await SeedAsync(publishedAt: distantPast));
        }

        var job = CreateJob(new FixedClock(distantPast + TimeSpan.FromDays(2)), batchSize: 2, maxBatchesPerCycle: 10);
        await job.PruneAsync(CancellationToken.None);

        await using var db = fixture.CreateDbContext();
        var remaining = await db.Set<Ago.Platform.Persistence.Postgres.OutboxMessage>()
            .CountAsync(o => ids.Contains(o.Id), CancellationToken.None);
        Assert.Equal(0, remaining);
    }

    /// <summary><see cref="OutboxPruneJobOptions.MaxBatchesPerCycle"/> is a safety valve: proves one
    /// cycle stops issuing batches once the cap is reached, leaving the rest for the next tick, rather
    /// than draining an arbitrarily large backlog in one go.</summary>
    [Fact]
    public async Task PruneAsync_StopsAtMaxBatchesPerCycle_LeavingTheRestForNextTime()
    {
        var distantPast = new DateTimeOffset(2001, 6, 1, 0, 0, 0, TimeSpan.Zero);
        var ids = new List<Guid>();
        for (var i = 0; i < 9; i++)
        {
            ids.Add(await SeedAsync(publishedAt: distantPast));
        }

        try
        {
            // batchSize 2 * maxBatchesPerCycle 3 = at most 6 of the 9 removed this cycle.
            var job = CreateJob(new FixedClock(distantPast + TimeSpan.FromDays(2)), batchSize: 2, maxBatchesPerCycle: 3);
            await job.PruneAsync(CancellationToken.None);

            await using var db = fixture.CreateDbContext();
            var remaining = await db.Set<Ago.Platform.Persistence.Postgres.OutboxMessage>()
                .CountAsync(o => ids.Contains(o.Id), CancellationToken.None);
            Assert.Equal(3, remaining);
        }
        finally
        {
            await using var db = fixture.CreateDbContext();
            var leftover = await db.Set<Ago.Platform.Persistence.Postgres.OutboxMessage>()
                .Where(o => ids.Contains(o.Id)).ToListAsync(CancellationToken.None);
            db.RemoveRange(leftover);
            await db.SaveChangesAsync(CancellationToken.None);
        }
    }

    /// <summary>`15-04`'s own Done-when: "each job's work is visible as a metric." Proves the shared
    /// heartbeat/rows/duration triad actually moves on a real prune, tagged <c>table="outbox"</c>.</summary>
    [Fact]
    public async Task PruneAsync_RecordsTheRetentionMetrics()
    {
        var id = await SeedAsync(publishedAt: Now - RetentionWindow - TimeSpan.FromMinutes(1));

        var exportedMetrics = new List<OpenTelemetry.Metrics.Metric>();
        using var meterProvider = Sdk.CreateMeterProviderBuilder()
            .AddMeter(ChatMetrics.MeterName)
            .AddInMemoryExporter(exportedMetrics)
            .Build();

        await CreateJob().PruneAsync(CancellationToken.None);
        meterProvider.ForceFlush();

        Assert.False(await OutboxRowExistsAsync(id));

        var cycles = exportedMetrics.Single(m => m.Name == ChatMetrics.RetentionPruneCyclesInstrumentName);
        long cycleCount = 0;
        foreach (ref readonly var point in cycles.GetMetricPoints())
        {
            cycleCount += point.GetSumLong();
        }
        Assert.True(cycleCount >= 1);

        var rows = exportedMetrics.Single(m => m.Name == ChatMetrics.RetentionRowsPrunedInstrumentName);
        long rowCount = 0;
        foreach (ref readonly var point in rows.GetMetricPoints())
        {
            rowCount += point.GetSumLong();
        }
        Assert.True(rowCount >= 1);
    }

    private OutboxPruneJob CreateJob(IClock? clock = null, int batchSize = 1000, int maxBatchesPerCycle = 50) =>
        new(fixture.DataSource, clock ?? new FixedClock(Now),
            Options.Create(new OutboxPruneJobOptions
            {
                RetentionWindow = RetentionWindow,
                BatchSize = batchSize,
                MaxBatchesPerCycle = maxBatchesPerCycle,
            }),
            NullLogger<OutboxPruneJob>.Instance);

    private async Task<Guid> SeedAsync(DateTimeOffset? publishedAt, DateTimeOffset? occurredAt = null)
    {
        var id = Guid.NewGuid();
        var message = new Ago.Platform.Persistence.Postgres.OutboxMessage(
            id, occurredAt ?? Now, "MessageAccepted", 1, "{}", $"key-{id:N}", Guid.NewGuid());
        if (publishedAt is { } value)
        {
            message.MarkPublished(value);
        }

        await using var db = fixture.CreateDbContext();
        db.Add(message);
        await db.SaveChangesAsync(CancellationToken.None);
        return id;
    }

    private async Task<bool> OutboxRowExistsAsync(Guid id)
    {
        await using var db = fixture.CreateDbContext();
        return await db.Set<Ago.Platform.Persistence.Postgres.OutboxMessage>().AnyAsync(o => o.Id == id, CancellationToken.None);
    }

    private async Task DeleteRowAsync(Guid id)
    {
        await using var db = fixture.CreateDbContext();
        var row = await db.Set<Ago.Platform.Persistence.Postgres.OutboxMessage>().FindAsync([id], CancellationToken.None);
        if (row is not null)
        {
            db.Remove(row);
            await db.SaveChangesAsync(CancellationToken.None);
        }
    }

    private sealed class FixedClock(DateTimeOffset now) : IClock
    {
        public DateTimeOffset UtcNow => now;
    }
}
