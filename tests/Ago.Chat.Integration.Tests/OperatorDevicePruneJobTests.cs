using Ago.Chat.Contracts;
using Ago.Chat.Domain;
using Ago.Chat.Infrastructure.Postgres;
using Ago.Chat.Worker;
using Ago.Platform.Kernel;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using OpenTelemetry;
using OpenTelemetry.Metrics;

namespace Ago.Chat.Integration.Tests;

/// <summary>
/// `26-123`/`adr/0185`: the fourth `operator_devices` revocation cause - real Postgres
/// (`testing.md`: never mock the database), the same shape `AccessRecordPruneJobTests`/
/// `OutboxPruneJobTests` already establish for a bounded-batch retention sweep, applied to an
/// `UPDATE` (revoke) rather than a `DELETE`. `PostgresFixture`'s container is shared with every other
/// test in <see cref="PostgresCollection"/> with no truncation between them
/// (`OperatorDeviceRepositoryTests`' own precedent for this exact table), so every seeded device below
/// gets a fresh <see cref="Guid"/> installation id and token rather than a literal.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class OperatorDevicePruneJobTests(PostgresFixture fixture)
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Threshold = TimeSpan.FromDays(14);

    [Fact]
    public async Task PruneAsync_RevokesADevice_NotSeenSinceBeforeTheThreshold()
    {
        var (siteId, operatorId) = await SeedSiteAndOperatorAsync();
        var installationId = await SeedDeviceAsync(siteId, operatorId, lastSeenAt: Now - Threshold - TimeSpan.FromMinutes(1));

        await CreateJob().PruneAsync(CancellationToken.None);

        var loaded = await LoadDeviceAsync(operatorId, installationId);
        Assert.NotNull(loaded!.RevokedAt);
        Assert.Equal(Now, loaded.RevokedAt);
    }

    [Fact]
    public async Task PruneAsync_LeavesADevice_SeenWithinTheThresholdAlone()
    {
        var (siteId, operatorId) = await SeedSiteAndOperatorAsync();
        var installationId = await SeedDeviceAsync(siteId, operatorId, lastSeenAt: Now - TimeSpan.FromDays(1));

        await CreateJob().PruneAsync(CancellationToken.None);

        var loaded = await LoadDeviceAsync(operatorId, installationId);
        Assert.Null(loaded!.RevokedAt);
    }

    /// <summary>Rule 5: a device already revoked - by sign-out, `TokenGone`, or an earlier run of this
    /// same job - is never touched a second time, and a cycle that finds nothing new to revoke reports
    /// zero, not the count of every already-revoked row it happened to scan past. Proves the query's own
    /// `revoked_at IS NULL` guard, not merely that <see cref="OperatorDevice.Revoke"/> is idempotent in
    /// isolation (`OperatorDeviceRepositoryTests`' own tests already cover that half).</summary>
    [Fact]
    public async Task PruneAsync_NeverTouchesAnAlreadyRevokedDevice()
    {
        var (siteId, operatorId) = await SeedSiteAndOperatorAsync();
        var installationId = await SeedDeviceAsync(
            siteId, operatorId, lastSeenAt: Now - Threshold - TimeSpan.FromDays(1),
            revokedAt: Now - TimeSpan.FromDays(1));

        var revokedThisCycle = await CreateJob().PruneAsync(CancellationToken.None);

        Assert.Equal(0, revokedThisCycle);
        var loaded = await LoadDeviceAsync(operatorId, installationId);
        // Untouched by this job - still carries its original revocation instant, not "Now".
        Assert.Equal(Now - TimeSpan.FromDays(1), loaded!.RevokedAt);
    }

    /// <summary>`15-04`'s own liveness rule, applied to this job: the shared retention heartbeat moves
    /// on a real prune, tagged <c>table="operator_devices"</c>, and (`26-123`'s own addition to
    /// `ago.chat.push.tokens_revoked`) the revoke is also visible under `cause="stale_timeout"` -
    /// `OutboxPruneJobTests.PruneAsync_RecordsTheRetentionMetrics`'s own shape.</summary>
    [Fact]
    public async Task PruneAsync_RecordsTheRetentionAndTokenRevokedMetrics()
    {
        var (siteId, operatorId) = await SeedSiteAndOperatorAsync();
        await SeedDeviceAsync(siteId, operatorId, lastSeenAt: Now - Threshold - TimeSpan.FromMinutes(1));

        var exportedMetrics = new List<Metric>();
        using var meterProvider = Sdk.CreateMeterProviderBuilder()
            .AddMeter(ChatMetrics.MeterName)
            .AddInMemoryExporter(exportedMetrics)
            .Build();

        var revoked = await CreateJob().PruneAsync(CancellationToken.None);
        meterProvider.ForceFlush();

        Assert.True(revoked >= 1);

        var cycles = exportedMetrics.Single(m => m.Name == ChatMetrics.RetentionPruneCyclesInstrumentName);
        Assert.True(SumLong(cycles) >= 1);

        var rows = exportedMetrics.Single(m => m.Name == ChatMetrics.RetentionRowsPrunedInstrumentName);
        Assert.True(SumLong(rows) >= 1);

        var tokensRevoked = exportedMetrics.Single(m => m.Name == ChatMetrics.PushTokensRevokedInstrumentName);
        Assert.True(SumLong(tokensRevoked) >= 1);
    }

    private static long SumLong(Metric metric)
    {
        long total = 0;
        foreach (ref readonly var point in metric.GetMetricPoints())
        {
            total += point.GetSumLong();
        }

        return total;
    }

    private OperatorDevicePruneJob CreateJob(int batchSize = 1000, int maxBatchesPerCycle = 50) =>
        new(fixture.DataSource, new FixedClock(Now),
            Options.Create(new OperatorDevicePruneJobOptions
            {
                Threshold = Threshold,
                BatchSize = batchSize,
                MaxBatchesPerCycle = maxBatchesPerCycle,
            }),
            NullLogger<OperatorDevicePruneJob>.Instance);

    private async Task<(SiteId SiteId, OperatorId OperatorId)> SeedSiteAndOperatorAsync()
    {
        var siteId = new SiteId(Guid.NewGuid());
        var operatorId = new OperatorId(Guid.NewGuid());
        await using var db = fixture.CreateDbContext();
        db.Sites.Add(new Site(siteId, $"site_{siteId.Value:N}", []));
        db.Operators.Add(new Operator(operatorId, siteId, OperatorStatus.Online, capacity: 5));
        await db.SaveChangesAsync();
        return (siteId, operatorId);
    }

    private async Task<string> SeedDeviceAsync(
        SiteId siteId, OperatorId operatorId, DateTimeOffset lastSeenAt, DateTimeOffset? revokedAt = null)
    {
        var installationId = $"installation-{Guid.NewGuid():N}";
        var device = OperatorDevice.Register(
            new OperatorDeviceId(Guid.NewGuid()), siteId, operatorId, installationId, PushProvider.RuStore,
            "android", $"token-{Guid.NewGuid():N}", lastSeenAt);
        if (revokedAt is { } revokedAtValue)
        {
            device.Revoke(revokedAtValue);
        }

        await using var db = fixture.CreateDbContext();
        await new OperatorDeviceRepository(db).SaveAsync(device, CancellationToken.None);
        return installationId;
    }

    private async Task<OperatorDevice?> LoadDeviceAsync(OperatorId operatorId, string installationId)
    {
        await using var db = fixture.CreateDbContext();
        return await new OperatorDeviceRepository(db).FindAsync(operatorId, installationId, CancellationToken.None);
    }

    private sealed class FixedClock(DateTimeOffset now) : IClock
    {
        public DateTimeOffset UtcNow => now;
    }
}
