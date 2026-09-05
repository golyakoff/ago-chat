using Ago.Chat.Api.Auth;
using Ago.Chat.Application.Abstractions;
using Ago.Chat.Application.UseCases.GetSiteByPublicKey;
using Ago.Chat.Application.UseCases.GetSiteInstallation;
using Ago.Chat.Domain;
using Ago.Chat.Infrastructure.Postgres;
using Ago.Platform.Caching.Redis;
using Ago.Platform.Hosting;
using Ago.Platform.Kernel;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Polly;
using Polly.Timeout;

namespace Ago.Chat.Integration.Tests;

/// <summary>
/// `23-06`'s own Done-when, proven against real Postgres (Testcontainers, no mocking -
/// `testing.md`) rather than the fake repository `Ago.Chat.Application.Tests` already exercises:
///
/// - "A visitor-session mint updates <c>last_seen_at</c> at most once a minute, proven by two mints
///   inside one minute and one row write."
/// - "A request from an origin not in the site's list records <c>last_refused_origin</c> and does
///   **not** update <c>last_seen_at</c>."
/// - "The site cache cannot serve a stale <c>last_seen_at</c> - an integration test that writes and
///   then reads through the API."
///
/// Uses <see cref="SiteCachingFixture"/> (real Postgres *and* Redis), not a plain
/// <c>PostgresFixture</c>, precisely for that third point - the whole question is whether
/// <see cref="Application.UseCases.GetSiteByPublicKey.GetSiteConfigByPublicKeyHandler"/>'s cached
/// <c>SiteConfigDto</c> (five-minute TTL, `caching.md`'s "the hot one") ever stands between a fresh
/// write and this read, and that requires a real cache actually running to disprove.
/// </summary>
[Collection(SiteCachingCollection.Name)]
public sealed class SiteInstallationSignalTests(SiteCachingFixture fixture)
{
    private static async Task<SiteId> SeedSiteAsync(SiteCachingFixture fixture, string allowedOrigin = "https://tenant.example")
    {
        var siteId = new SiteId(Guid.NewGuid());
        await using var db = fixture.CreateDbContext();
        db.Sites.Add(new Site(siteId, $"site_{siteId.Value:N}", [allowedOrigin]));
        await db.SaveChangesAsync();
        return siteId;
    }

    [Fact]
    public async Task RecordSightingAsync_CalledTwiceWithinOneMinute_WritesTheRowOnlyOnce()
    {
        var siteId = await SeedSiteAsync(fixture);
        var repository = new SiteInstallationSignalRepository(fixture.DataSource);
        var firstMint = new DateTimeOffset(2026, 9, 5, 12, 0, 0, TimeSpan.Zero);
        var secondMintInsideTheWindow = firstMint.AddSeconds(30);

        await repository.RecordSightingAsync(siteId, firstMint, CancellationToken.None);
        await repository.RecordSightingAsync(siteId, secondMintInsideTheWindow, CancellationToken.None);

        var signals = await repository.GetAsync(siteId, CancellationToken.None);
        Assert.Equal(firstMint, signals.LastSeenAt);
        Assert.Equal(firstMint, signals.FirstSeenAt);
    }

    [Fact]
    public async Task RecordSightingAsync_CalledAgainAfterTheThrottleWindow_UpdatesTheRow()
    {
        var siteId = await SeedSiteAsync(fixture);
        var repository = new SiteInstallationSignalRepository(fixture.DataSource);
        var firstMint = new DateTimeOffset(2026, 9, 5, 12, 0, 0, TimeSpan.Zero);
        var laterMint = firstMint.AddMinutes(2);

        await repository.RecordSightingAsync(siteId, firstMint, CancellationToken.None);
        await repository.RecordSightingAsync(siteId, laterMint, CancellationToken.None);

        var signals = await repository.GetAsync(siteId, CancellationToken.None);
        Assert.Equal(laterMint, signals.LastSeenAt);
        // first_seen_at is written once and never moves, even once the throttle window has passed -
        // it answers "when did this begin", not "when was it last confirmed".
        Assert.Equal(firstMint, signals.FirstSeenAt);
    }

    /// <summary>The refusal path never touches <c>last_seen_at</c> - the two columns are written by
    /// two different statements, and a site that has only ever been refused must still read as
    /// unseen.</summary>
    [Fact]
    public async Task RecordRefusedOriginAsync_DoesNotUpdateLastSeenAt()
    {
        var siteId = await SeedSiteAsync(fixture);
        var repository = new SiteInstallationSignalRepository(fixture.DataSource);
        var refusedAt = new DateTimeOffset(2026, 9, 5, 12, 0, 0, TimeSpan.Zero);

        await repository.RecordRefusedOriginAsync(siteId, "https://www.tenant.example", refusedAt, CancellationToken.None);

        var signals = await repository.GetAsync(siteId, CancellationToken.None);
        Assert.Equal("https://www.tenant.example", signals.LastRefusedOrigin);
        Assert.Equal(refusedAt, signals.LastRefusedOriginAt);
        Assert.Null(signals.LastSeenAt);
        Assert.Null(signals.FirstSeenAt);
    }

    /// <summary>The refusal write's own once-a-minute throttle, proven against real Postgres the same
    /// way the sighting write's is above.</summary>
    [Fact]
    public async Task RecordRefusedOriginAsync_CalledTwiceWithinOneMinute_WritesTheRowOnlyOnce()
    {
        var siteId = await SeedSiteAsync(fixture);
        var repository = new SiteInstallationSignalRepository(fixture.DataSource);
        var first = new DateTimeOffset(2026, 9, 5, 12, 0, 0, TimeSpan.Zero);
        var secondInsideTheWindow = first.AddSeconds(30);

        await repository.RecordRefusedOriginAsync(siteId, "https://www.tenant.example", first, CancellationToken.None);
        await repository.RecordRefusedOriginAsync(siteId, "https://staging.tenant.example", secondInsideTheWindow, CancellationToken.None);

        var signals = await repository.GetAsync(siteId, CancellationToken.None);
        Assert.Equal("https://www.tenant.example", signals.LastRefusedOrigin);
        Assert.Equal(first, signals.LastRefusedOriginAt);
    }

    /// <summary>
    /// The Done-when item this file exists for: "the site cache cannot serve a stale
    /// <c>last_seen_at</c> - an integration test that writes and then reads through the API." Real
    /// Redis is warmed with this site's cached <c>SiteConfigDto</c> first - the widget handshake's own
    /// hot path (`caching.md`) - specifically so this test can prove the *install* read is unaffected
    /// by that cache being warm, rather than merely never having been populated.
    /// </summary>
    [Fact]
    public async Task GetSiteInstallationHandler_AfterASighting_SeesItImmediately_EvenWithTheSiteConfigCacheWarm()
    {
        var siteId = await SeedSiteAsync(fixture);
        var operatorId = new OperatorId(Guid.NewGuid());
        var publicKey = $"site_{siteId.Value:N}";

        // Warm the *other* cache - GetSiteConfigByPublicKeyHandler's SiteConfigDto - the same call the
        // widget handshake makes on every page load. If the install read were ever wired through this
        // cache (or a copy of it), this is what would let a stale value leak through.
        var cache = new RedisCache(
            fixture.RedisMultiplexer,
            new ResiliencePipelineBuilder().AddTimeout(TimeSpan.FromSeconds(2)).Build(),
            NullLogger<RedisCache>.Instance);
        var siteConfigHandler = new GetSiteConfigByPublicKeyHandler(new SiteRepository(fixture.CreateDbContext()), cache);
        await siteConfigHandler.HandleAsync(new GetSiteConfigByPublicKey(publicKey), CancellationToken.None);

        var permissions = new AlwaysAllowPermissionChecker();
        var signalRepository = new SiteInstallationSignalRepository(fixture.DataSource);
        var conversationReadStore = new ConversationReadStore(fixture.DataSource);
        var now = new DateTimeOffset(2026, 9, 5, 12, 0, 0, TimeSpan.Zero);
        var handler = new GetSiteInstallationHandler(
            new SiteRepository(fixture.CreateDbContext()), permissions, signalRepository, conversationReadStore,
            new FixedClock(now), new SiteInstallationOptions());

        var before = await handler.HandleAsync(new GetSiteInstallation(siteId, operatorId), CancellationToken.None);
        Assert.True(before.IsSuccess);
        Assert.Null(before.Value.LastSeenAt);

        await signalRepository.RecordSightingAsync(siteId, now, CancellationToken.None);

        var after = await handler.HandleAsync(new GetSiteInstallation(siteId, operatorId), CancellationToken.None);
        Assert.True(after.IsSuccess);
        Assert.Equal(now, after.Value.LastSeenAt);
        Assert.Equal(SiteInstallationState.SeenAndQuiet, after.Value.State);
    }

    /// <summary>
    /// The wiring itself, not just the repository it calls - `AuthEndpoints.HandleVisitorSessionAsync`
    /// is invoked directly (the same "construct it directly, no full server" seam that method's own
    /// doc comment describes), so this proves the endpoint actually calls
    /// <see cref="ISiteInstallationSignalRepository.RecordSightingAsync"/> on a successful mint, not
    /// merely that the repository behaves correctly in isolation (already proven above).
    /// </summary>
    [Fact]
    public async Task HandleVisitorSessionAsync_OnASuccessfulMint_RecordsASighting()
    {
        var siteId = await SeedSiteAsync(fixture);
        var publicKey = $"site_{siteId.Value:N}";
        var getSite = new GetSiteConfigByPublicKeyHandler(new SiteRepository(fixture.CreateDbContext()), CreateCache());
        var signalRepository = new SiteInstallationSignalRepository(fixture.DataSource);
        var tokens = new JwtTokenService(TestSigningKeys.Ring(), "test-issuer", new SystemClock());
        var httpContext = BuildHttpContext(origin: null);

        var result = await AuthEndpoints.HandleVisitorSessionAsync(
            new AuthEndpoints.VisitorSessionRequest(publicKey), getSite, signalRepository, new FakeRateLimiter(),
            Options.Create(new VisitorSessionRateLimitOptions()), new UuidV7Generator(), new SystemClock(), tokens,
            httpContext, CancellationToken.None);
        await result.ExecuteAsync(httpContext);

        Assert.Equal(StatusCodes.Status201Created, httpContext.Response.StatusCode);
        var signals = await signalRepository.GetAsync(siteId, CancellationToken.None);
        Assert.NotNull(signals.LastSeenAt);
        Assert.NotNull(signals.FirstSeenAt);
    }

    /// <summary>The refusal side of the same wiring - a mint from a disallowed origin records the
    /// refusal and never the sighting, through the real endpoint, not just the repository.</summary>
    [Fact]
    public async Task HandleVisitorSessionAsync_WhenTheOriginIsRefused_RecordsTheRefusal_NotASighting()
    {
        var siteId = await SeedSiteAsync(fixture, allowedOrigin: "https://tenant.example");
        var publicKey = $"site_{siteId.Value:N}";
        var getSite = new GetSiteConfigByPublicKeyHandler(new SiteRepository(fixture.CreateDbContext()), CreateCache());
        var signalRepository = new SiteInstallationSignalRepository(fixture.DataSource);
        var tokens = new JwtTokenService(TestSigningKeys.Ring(), "test-issuer", new SystemClock());
        var httpContext = BuildHttpContext(origin: "https://www.tenant.example");

        var result = await AuthEndpoints.HandleVisitorSessionAsync(
            new AuthEndpoints.VisitorSessionRequest(publicKey), getSite, signalRepository, new FakeRateLimiter(),
            Options.Create(new VisitorSessionRateLimitOptions()), new UuidV7Generator(), new SystemClock(), tokens,
            httpContext, CancellationToken.None);
        await result.ExecuteAsync(httpContext);

        Assert.Equal(StatusCodes.Status403Forbidden, httpContext.Response.StatusCode);
        var signals = await signalRepository.GetAsync(siteId, CancellationToken.None);
        Assert.Equal("https://www.tenant.example", signals.LastRefusedOrigin);
        Assert.Null(signals.LastSeenAt);
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

    private sealed class AlwaysAllowPermissionChecker : IPermissionChecker
    {
        public Task<bool> HasPermissionAsync(OperatorId operatorId, SiteId siteId, Permission permission, CancellationToken cancellationToken) =>
            Task.FromResult(true);

        public Task<IReadOnlyList<string>> GetPermissionsAsync(OperatorId operatorId, SiteId siteId, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<string>>([]);

        // `23-26`: this suite's own subject is the installation signal, not RemoveOperator's guard -
        // never called here.
        public Task<int> CountNonRemovedHoldersAsync(SiteId siteId, Permission permission, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Not part of the installation-signal path under test.");
    }
}
