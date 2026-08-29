using Ago.Chat.Application.Abstractions;
using Ago.Chat.Contracts;
using Ago.Chat.Domain;
using Ago.Chat.Worker;
using Ago.Platform.Kernel;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using OpenTelemetry;
using OpenTelemetry.Metrics;

namespace Ago.Chat.Integration.Tests;

/// <summary>
/// `15-04`/`adr/0031`: the drop half of partition pruning, real Postgres, a real partition actually
/// dropped - not asserted against an empty table (the item's own Done-when: "at least one real
/// partition drop"). Every partition this suite creates is far in the past (year 2000-2001) so it can
/// never collide with anything <see cref="PartitionMaintenanceJob"/> or normal message-insert traffic
/// in this shared fixture creates or touches, and every test drops its own leftover partition in
/// <c>finally</c> regardless of outcome.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class MessagePartitionPruneJobTests(PostgresFixture fixture)
{
    private const int RetentionHorizonMonths = 3;

    [Fact]
    public async Task PruneAsync_DropsAPartitionPastTheHorizon_WhenTheArchiveGateConfirms()
    {
        var partitionName = await CreatePartitionAsync(2000, 1);
        try
        {
            var job = CreateJob(referenceNow: new DateTimeOffset(2000, 6, 1, 0, 0, 0, TimeSpan.Zero), gate: new FakeArchiveGate(confirmed: true));
            await job.PruneAsync(CancellationToken.None);

            Assert.False(await PartitionExistsAsync(partitionName));
        }
        finally
        {
            await DropIfExistsAsync(partitionName);
        }
    }

    [Fact]
    public async Task PruneAsync_LeavesAPartitionPastTheHorizon_WhenTheArchiveGateDoesNotConfirm()
    {
        var partitionName = await CreatePartitionAsync(2000, 2);
        try
        {
            var job = CreateJob(referenceNow: new DateTimeOffset(2000, 6, 1, 0, 0, 0, TimeSpan.Zero), gate: new FakeArchiveGate(confirmed: false));
            await job.PruneAsync(CancellationToken.None);

            Assert.True(await PartitionExistsAsync(partitionName));
        }
        finally
        {
            await DropIfExistsAsync(partitionName);
        }
    }

    /// <summary>A partition inside the horizon is never even offered to the gate - proves the horizon
    /// check comes first, so a real (future) archive-confirmation implementation is never asked about a
    /// period that has not been archived yet by design, only ones that plausibly could have been.</summary>
    [Fact]
    public async Task PruneAsync_NeverConsultsTheGate_ForAPartitionInsideTheHorizon()
    {
        var partitionName = await CreatePartitionAsync(2000, 5);
        try
        {
            var gate = new FakeArchiveGate(confirmed: true);
            // referenceNow is the same month the partition covers - well inside any positive horizon.
            var job = CreateJob(referenceNow: new DateTimeOffset(2000, 5, 15, 0, 0, 0, TimeSpan.Zero), gate: gate);
            await job.PruneAsync(CancellationToken.None);

            Assert.True(await PartitionExistsAsync(partitionName));
            Assert.DoesNotContain(partitionName, gate.CheckedPartitions);
        }
        finally
        {
            await DropIfExistsAsync(partitionName);
        }
    }

    /// <summary>`13-06` replaced `15-04`'s real-default gate (`AlwaysConfirmedMessageArchiveGate`) with
    /// the object-storage-backed <c>MessageArchiveGate</c> (its own integration coverage lives in
    /// `MessageArchiveEndToEndTests`) - `AlwaysConfirmedMessageArchiveGate` is kept only as a permissive
    /// test fake now (that class's own remarks), so this test proves the same thing it always did -
    /// the job's own wiring genuinely drops under a confirming gate, not just that the type compiles -
    /// against whichever gate a caller hands it.</summary>
    [Fact]
    public async Task PruneAsync_WithAConfirmingFakeGate_DropsAPartitionPastTheHorizon()
    {
        var partitionName = await CreatePartitionAsync(2001, 1);
        try
        {
            var job = CreateJob(referenceNow: new DateTimeOffset(2001, 6, 1, 0, 0, 0, TimeSpan.Zero), gate: new AlwaysConfirmedMessageArchiveGate());
            await job.PruneAsync(CancellationToken.None);

            Assert.False(await PartitionExistsAsync(partitionName));
        }
        finally
        {
            await DropIfExistsAsync(partitionName);
        }
    }

    [Fact]
    public async Task PruneAsync_RecordsTheRetentionMetrics()
    {
        var dropped = await CreatePartitionAsync(1999, 11);
        var pending = await CreatePartitionAsync(1999, 12);
        try
        {
            var exportedMetrics = new List<Metric>();
            using var meterProvider = Sdk.CreateMeterProviderBuilder()
                .AddMeter(ChatMetrics.MeterName)
                .AddInMemoryExporter(exportedMetrics)
                .Build();

            var gate = new SelectiveArchiveGate(confirmedFor: dropped);
            var job = CreateJob(referenceNow: new DateTimeOffset(2000, 6, 1, 0, 0, 0, TimeSpan.Zero), gate: gate);
            await job.PruneAsync(CancellationToken.None);
            meterProvider.ForceFlush();

            Assert.False(await PartitionExistsAsync(dropped));
            Assert.True(await PartitionExistsAsync(pending));

            var droppedMetric = exportedMetrics.Single(m => m.Name == ChatMetrics.RetentionPartitionsDroppedInstrumentName);
            long droppedCount = 0;
            foreach (ref readonly var point in droppedMetric.GetMetricPoints())
            {
                droppedCount += point.GetSumLong();
            }
            Assert.Equal(1, droppedCount);

            var pendingMetric = exportedMetrics.Single(m => m.Name == ChatMetrics.RetentionPartitionsPendingArchiveInstrumentName);
            long pendingCount = 0;
            foreach (ref readonly var point in pendingMetric.GetMetricPoints())
            {
                pendingCount += point.GetSumLong();
            }
            Assert.Equal(1, pendingCount);
        }
        finally
        {
            await DropIfExistsAsync(dropped);
            await DropIfExistsAsync(pending);
        }
    }

    private MessagePartitionPruneJob CreateJob(DateTimeOffset referenceNow, IMessageArchiveGate gate) =>
        new(fixture.DataSource, gate, new FakeFileStorage(), new FixedClock(referenceNow),
            Options.Create(new MessagePartitionPruneJobOptions { RetentionHorizonMonths = RetentionHorizonMonths }),
            NullLogger<MessagePartitionPruneJob>.Instance);

    // `13-06`: messages is now PARTITION BY LIST (retention_class) then RANGE (created_at) - every
    // leaf this suite creates hangs off the `free` class partition (already created by this item's own
    // migration), the same class every test in this file already implicitly meant before classes
    // existed at all. MessagePartitionNames is the same helper MessagePartitionPruneQuery itself uses,
    // so a name built here and one parsed back by ListPartitionsAsync can never drift apart.
    private async Task<string> CreatePartitionAsync(int year, int month)
    {
        var from = new DateOnly(year, month, 1);
        var to = from.AddMonths(1);
        var name = MessagePartitionNames.ForMonth(RetentionClass.Free, new DateTimeOffset(from.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero));

        await using var connection = await fixture.DataSource.OpenConnectionAsync();
        var sql = $"""
            CREATE TABLE IF NOT EXISTS {name} PARTITION OF {MessagePartitionNames.ForClass(RetentionClass.Free)}
                FOR VALUES FROM ('{from:yyyy-MM-dd}') TO ('{to:yyyy-MM-dd}');
            """;
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
        return name;
    }

    private async Task<bool> PartitionExistsAsync(string partitionName)
    {
        await using var connection = await fixture.DataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand("SELECT count(*) FROM pg_class WHERE relname = @name", connection);
        command.Parameters.AddWithValue("name", partitionName);
        return (long)(await command.ExecuteScalarAsync())! > 0;
    }

    private async Task DropIfExistsAsync(string partitionName)
    {
        await using var connection = await fixture.DataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand($"DROP TABLE IF EXISTS {partitionName};", connection);
        await command.ExecuteNonQueryAsync();
    }

    private sealed class FixedClock(DateTimeOffset now) : IClock
    {
        public DateTimeOffset UtcNow => now;
    }

    private sealed class FakeArchiveGate(bool confirmed) : IMessageArchiveGate
    {
        public List<string> CheckedPartitions { get; } = [];

        public Task<bool> IsArchivedAsync(string partitionName, DateOnly periodStart, DateOnly periodEnd, CancellationToken cancellationToken)
        {
            CheckedPartitions.Add(partitionName);
            return Task.FromResult(confirmed);
        }
    }

    private sealed class SelectiveArchiveGate(string confirmedFor) : IMessageArchiveGate
    {
        public Task<bool> IsArchivedAsync(string partitionName, DateOnly periodStart, DateOnly periodEnd, CancellationToken cancellationToken) =>
            Task.FromResult(partitionName == confirmedFor);
    }
}
