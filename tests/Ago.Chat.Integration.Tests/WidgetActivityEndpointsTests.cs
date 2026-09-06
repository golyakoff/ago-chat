using Ago.Chat.Api.WidgetActivity;
using Ago.Chat.Application.Abstractions;
using Ago.Chat.Application.UseCases.GetSiteByPublicKey;
using Ago.Chat.Domain;
using Ago.Chat.Infrastructure.Postgres;
using Ago.Chat.Infrastructure.Postgres.Pipeline;
using Ago.Chat.Module.Pipeline;
using Ago.Platform.Caching.Redis;
using Ago.Platform.Kernel;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Polly;
using Polly.Timeout;

namespace Ago.Chat.Integration.Tests;

/// <summary>
/// `23-07`'s own Done-when, proven against real Postgres (Testcontainers, no mocking -
/// `testing.md`), the identical "construct the endpoint directly, no full server" seam
/// <c>SiteInstallationSignalTests</c>'s own remarks describe for the mint endpoint:
///
/// - "A beacon from an allowed origin counts a load and updates `last_seen_at` at most once a
///   minute."
/// - "A beacon from a refused origin counts nothing and records the refusal."
/// - "The beacon writes no row synchronously" - proven by calling the endpoint with no
///   <see cref="WidgetActivityWriter"/> ever in reach of it at all: only <see
///   cref="WidgetActivityAccumulator"/> is passed as the <see cref="IWidgetActivityRecorder"/>, so a
///   test that reached Postgres for the count would fail with a null-reference, not merely look wrong.
/// - "Two beacons inside one flush window produce one row write, not two."
/// - "A site's counters are never readable by another tenant."
/// </summary>
[Collection(SiteCachingCollection.Name)]
public sealed class WidgetActivityEndpointsTests(SiteCachingFixture fixture)
{
    private static readonly DateTimeOffset Now = new(2026, 9, 6, 12, 0, 0, TimeSpan.Zero);

    private static async Task<SiteId> SeedSiteAsync(SiteCachingFixture fixture, string allowedOrigin = "https://tenant.example")
    {
        var siteId = new SiteId(Guid.NewGuid());
        await using var db = fixture.CreateDbContext();
        db.Sites.Add(new Site(siteId, $"site_{siteId.Value:N}", [allowedOrigin]));
        await db.SaveChangesAsync();
        return siteId;
    }

    [Fact]
    public async Task HandleAsync_FromAnAllowedOrigin_WithLoadKind_RecordsALoadAndUpdatesLastSeenAt()
    {
        var siteId = await SeedSiteAsync(fixture);
        var publicKey = $"site_{siteId.Value:N}";
        var getSite = new GetSiteConfigByPublicKeyHandler(new SiteRepository(fixture.CreateDbContext()), CreateCache());
        var signalRepository = new SiteInstallationSignalRepository(fixture.DataSource);
        var accumulator = new WidgetActivityAccumulator();
        var httpContext = BuildHttpContext(origin: "https://tenant.example");

        var result = await WidgetActivityEndpoints.HandleAsync(
            new WidgetActivityEndpoints.WidgetActivityBeaconRequest(publicKey, "load"),
            getSite, signalRepository, accumulator, new FakeRateLimiter(),
            Options.Create(new WidgetActivityBeaconRateLimitOptions()), new FixedClock(Now), httpContext, CancellationToken.None);
        await result.ExecuteAsync(httpContext);

        Assert.Equal(StatusCodes.Status204NoContent, httpContext.Response.StatusCode);

        var signals = await signalRepository.GetAsync(siteId, CancellationToken.None);
        Assert.Equal(Now, signals.LastSeenAt);

        var deltas = accumulator.DrainSnapshot();
        var delta = Assert.Single(deltas);
        Assert.Equal(siteId, delta.SiteId);
        Assert.Equal(1, delta.Loads);
        Assert.Equal(0, delta.Opens);
        Assert.Equal(0, delta.Conversations);
    }

    [Fact]
    public async Task HandleAsync_WithOpenKind_RecordsAnOpen()
    {
        var siteId = await SeedSiteAsync(fixture);
        var publicKey = $"site_{siteId.Value:N}";
        var getSite = new GetSiteConfigByPublicKeyHandler(new SiteRepository(fixture.CreateDbContext()), CreateCache());
        var signalRepository = new SiteInstallationSignalRepository(fixture.DataSource);
        var accumulator = new WidgetActivityAccumulator();
        var httpContext = BuildHttpContext(origin: "https://tenant.example");

        var result = await WidgetActivityEndpoints.HandleAsync(
            new WidgetActivityEndpoints.WidgetActivityBeaconRequest(publicKey, "open"),
            getSite, signalRepository, accumulator, new FakeRateLimiter(),
            Options.Create(new WidgetActivityBeaconRateLimitOptions()), new FixedClock(Now), httpContext, CancellationToken.None);
        await result.ExecuteAsync(httpContext);

        Assert.Equal(StatusCodes.Status204NoContent, httpContext.Response.StatusCode);
        var delta = Assert.Single(accumulator.DrainSnapshot());
        Assert.Equal(0, delta.Loads);
        Assert.Equal(1, delta.Opens);
    }

    /// <summary>The item's own Done-when: "A beacon from a refused origin counts nothing and records
    /// the refusal" - both halves asserted together, the way the item states them.</summary>
    [Fact]
    public async Task HandleAsync_FromARefusedOrigin_CountsNothingAndRecordsTheRefusal()
    {
        var siteId = await SeedSiteAsync(fixture, allowedOrigin: "https://tenant.example");
        var publicKey = $"site_{siteId.Value:N}";
        var getSite = new GetSiteConfigByPublicKeyHandler(new SiteRepository(fixture.CreateDbContext()), CreateCache());
        var signalRepository = new SiteInstallationSignalRepository(fixture.DataSource);
        var accumulator = new WidgetActivityAccumulator();
        var httpContext = BuildHttpContext(origin: "https://www.tenant.example");

        var result = await WidgetActivityEndpoints.HandleAsync(
            new WidgetActivityEndpoints.WidgetActivityBeaconRequest(publicKey, "load"),
            getSite, signalRepository, accumulator, new FakeRateLimiter(),
            Options.Create(new WidgetActivityBeaconRateLimitOptions()), new FixedClock(Now), httpContext, CancellationToken.None);
        await result.ExecuteAsync(httpContext);

        Assert.Equal(StatusCodes.Status403Forbidden, httpContext.Response.StatusCode);

        var signals = await signalRepository.GetAsync(siteId, CancellationToken.None);
        Assert.Equal("https://www.tenant.example", signals.LastRefusedOrigin);
        Assert.Null(signals.LastSeenAt);

        // Nothing accumulated at all - the refused branch returns before either RecordLoad/RecordOpen
        // is ever reached (WidgetActivityEndpoints.HandleAsync's own ordering).
        Assert.Empty(accumulator.DrainSnapshot());
    }

    [Fact]
    public async Task HandleAsync_WithAnUnknownKind_IsRefused()
    {
        var siteId = await SeedSiteAsync(fixture);
        var publicKey = $"site_{siteId.Value:N}";
        var getSite = new GetSiteConfigByPublicKeyHandler(new SiteRepository(fixture.CreateDbContext()), CreateCache());
        var signalRepository = new SiteInstallationSignalRepository(fixture.DataSource);
        var accumulator = new WidgetActivityAccumulator();
        var httpContext = BuildHttpContext(origin: "https://tenant.example");

        var result = await WidgetActivityEndpoints.HandleAsync(
            new WidgetActivityEndpoints.WidgetActivityBeaconRequest(publicKey, "close"),
            getSite, signalRepository, accumulator, new FakeRateLimiter(),
            Options.Create(new WidgetActivityBeaconRateLimitOptions()), new FixedClock(Now), httpContext, CancellationToken.None);
        await result.ExecuteAsync(httpContext);

        Assert.Equal(StatusCodes.Status400BadRequest, httpContext.Response.StatusCode);
        Assert.Empty(accumulator.DrainSnapshot());
    }

    /// <summary>The item's own Done-when: "Two beacons inside one flush window produce one row write,
    /// not two" - proven at the level that actually decides it, the accumulate-then-flush pair, rather
    /// than by calling the HTTP endpoint twice and hoping the timing lines up.</summary>
    [Fact]
    public async Task AccumulateThenFlush_TwoLoadsInOneWindow_ProducesOneRowWithBothCounted()
    {
        var siteId = await SeedSiteAsync(fixture);
        var accumulator = new WidgetActivityAccumulator();

        accumulator.RecordLoad(siteId, Now);
        accumulator.RecordLoad(siteId, Now.AddSeconds(3));

        var writer = new WidgetActivityWriter(fixture.DataSource);
        await writer.FlushAsync(accumulator.DrainSnapshot(), CancellationToken.None);

        var readStore = new WidgetActivityReadStore(fixture.DataSource);
        var totals = await readStore.GetTotalsAsync(siteId, DateOnly.FromDateTime(Now.UtcDateTime), CancellationToken.None);
        Assert.Equal(2, totals.Loads);
    }

    /// <summary>A second flush for the same (site, day) adds to the row rather than replacing it -
    /// the `ON CONFLICT ... DO UPDATE = table.column + excluded.column` half of the same Done-when,
    /// exercised across two separate flush calls rather than one.</summary>
    [Fact]
    public async Task Flush_CalledTwiceForTheSameSiteAndDay_AddsRatherThanReplaces()
    {
        var siteId = await SeedSiteAsync(fixture);
        var writer = new WidgetActivityWriter(fixture.DataSource);

        await writer.FlushAsync([new WidgetActivityDelta(siteId, DateOnly.FromDateTime(Now.UtcDateTime), 5, 2, 1)], CancellationToken.None);
        await writer.FlushAsync([new WidgetActivityDelta(siteId, DateOnly.FromDateTime(Now.UtcDateTime), 3, 1, 0)], CancellationToken.None);

        var readStore = new WidgetActivityReadStore(fixture.DataSource);
        var totals = await readStore.GetTotalsAsync(siteId, DateOnly.FromDateTime(Now.UtcDateTime), CancellationToken.None);
        Assert.Equal(8, totals.Loads);
        Assert.Equal(3, totals.Opens);
        Assert.Equal(1, totals.Conversations);
    }

    /// <summary>The item's own Done-when: "A site's counters are never readable by another tenant" -
    /// two sites' rows exist in the same table, and each site's own read returns only its own.</summary>
    [Fact]
    public async Task GetTotalsAsync_NeverReturnsAnotherSitesCounts()
    {
        var siteA = await SeedSiteAsync(fixture, allowedOrigin: "https://a.example");
        var siteB = await SeedSiteAsync(fixture, allowedOrigin: "https://b.example");
        var writer = new WidgetActivityWriter(fixture.DataSource);
        var day = DateOnly.FromDateTime(Now.UtcDateTime);

        await writer.FlushAsync(
            [new WidgetActivityDelta(siteA, day, 10, 5, 2), new WidgetActivityDelta(siteB, day, 100, 50, 20)],
            CancellationToken.None);

        var readStore = new WidgetActivityReadStore(fixture.DataSource);
        var totalsA = await readStore.GetTotalsAsync(siteA, day, CancellationToken.None);
        var totalsB = await readStore.GetTotalsAsync(siteB, day, CancellationToken.None);

        Assert.Equal(10, totalsA.Loads);
        Assert.Equal(100, totalsB.Loads);
    }

    private RedisCache CreateCache() => new(
        fixture.RedisMultiplexer, new ResiliencePipelineBuilder().AddTimeout(TimeSpan.FromSeconds(2)).Build(), NullLogger<RedisCache>.Instance);

    private static DefaultHttpContext BuildHttpContext(string? origin)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(Options.Create(new Microsoft.AspNetCore.Http.Json.JsonOptions()));
        var httpContext = new DefaultHttpContext
        {
            RequestServices = services.BuildServiceProvider(),
            Response = { Body = new MemoryStream() },
        };
        if (origin is not null)
        {
            httpContext.Request.Headers.Origin = origin;
        }

        return httpContext;
    }

    private sealed class FixedClock(DateTimeOffset now) : IClock
    {
        public DateTimeOffset UtcNow { get; } = now;
    }
}
