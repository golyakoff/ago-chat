using System.Diagnostics;
using Ago.Chat.Api.Auth;
using Ago.Chat.Application.UseCases.GetSiteByPublicKey;
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

namespace Ago.Chat.Integration.Tests;

/// <summary>
/// `22-08`/`docs/backlog/22-08-*.md`'s own Done-when: "suspending an account... stops the widget
/// being served for a new chat session", proven with the observed elapsed time - against real
/// Postgres and real Redis (the same <see cref="SiteCachingFixture"/> <see cref="OriginAuthorizationTests"/>
/// already uses for this identical endpoint), not asserted from the code.
///
/// <para><b>Why this proves the effect is immediate, not merely eventual.</b>
/// <see cref="ISiteSuspensionReadStore"/> is a live, uncached Dapper read (<c>Site.SuspendedUntil</c>'s
/// own remarks) - unlike the widget's own <c>SiteConfigDto</c>, which sits behind a 5-minute cache this
/// same endpoint also reads. Suspending a site writes <c>sites.suspended_until</c> directly; the very
/// next call to <c>HandleVisitorSessionAsync</c> - with no cache to invalidate, no event to wait for -
/// already sees it. The elapsed time this test measures is therefore just the write's own commit plus
/// one more indexed read, not a propagation delay of any kind.</para>
/// </summary>
[Collection(SiteCachingCollection.Name)]
public sealed class TenantSuspensionSessionGateTests(SiteCachingFixture fixture)
{
    [Fact]
    public async Task VisitorSessionEndpoint_ForASuspendedSite_RefusesANewSession()
    {
        var (publicKey, siteId) = await SeedSiteAsync();

        // Baseline: an ordinary, unsuspended site mints a session normally.
        Assert.Equal(StatusCodes.Status201Created, await InvokeVisitorSessionAsync(publicKey));

        var stopwatch = Stopwatch.StartNew();
        await SuspendSiteAsync(siteId, DateTimeOffset.UtcNow.AddMinutes(30));

        var status = await InvokeVisitorSessionAsync(publicKey);
        stopwatch.Stop();

        Assert.Equal(StatusCodes.Status403Forbidden, status);

        // `docs/backlog/22-08-*.md`'s own "with the observed elapsed time in the report" - recorded
        // to a file the worker's own report reads back. Sub-second by construction (this handler's
        // own class remarks state why): a live read against the same row the suspend write just
        // committed, never a cache or a broker hop.
        File.WriteAllText(
            Path.Combine(Path.GetTempPath(), "22-08-session-gate-ms.txt"),
            stopwatch.ElapsedMilliseconds.ToString());
    }

    [Fact]
    public async Task VisitorSessionEndpoint_ForASiteWhoseSuspensionHasAlreadyPassed_MintsNormally()
    {
        var (publicKey, siteId) = await SeedSiteAsync();
        await SuspendSiteAsync(siteId, DateTimeOffset.UtcNow.AddSeconds(-1));

        var status = await InvokeVisitorSessionAsync(publicKey);

        Assert.Equal(StatusCodes.Status201Created, status);
    }

    [Fact]
    public async Task VisitorSessionEndpoint_OnceUnblocked_MintsNormallyAgain()
    {
        var (publicKey, siteId) = await SeedSiteAsync();
        await SuspendSiteAsync(siteId, DateTimeOffset.UtcNow.AddMinutes(30));
        Assert.Equal(StatusCodes.Status403Forbidden, await InvokeVisitorSessionAsync(publicKey));

        await SuspendSiteAsync(siteId, null);

        var status = await InvokeVisitorSessionAsync(publicKey);
        Assert.Equal(StatusCodes.Status201Created, status);
    }

    private async Task<(string PublicKey, SiteId SiteId)> SeedSiteAsync()
    {
        var siteId = new SiteId(Guid.NewGuid());
        var publicKey = $"site_{siteId.Value:N}";
        await using var db = fixture.CreateDbContext();
        db.Sites.Add(new Site(siteId, publicKey, ["https://shop.example"]));
        await db.SaveChangesAsync();
        return (publicKey, siteId);
    }

    /// <summary>Suspends (or lifts, for <paramref name="until"/> <see langword="null"/>) directly
    /// through <see cref="Site.Suspend"/>/<see cref="Site.LiftSuspension"/> and
    /// <see cref="SiteRepository.SaveAsync"/> - the identical write path
    /// <c>SuspendTenantAsOwnerHandler</c> uses, minus the audit record and the outbox publish, neither
    /// of which this gate's own read depends on.</summary>
    private async Task SuspendSiteAsync(SiteId siteId, DateTimeOffset? until)
    {
        await using var db = fixture.CreateDbContext();
        var repository = new SiteRepository(db);
        var site = await repository.GetByIdAsync(siteId, CancellationToken.None);
        if (site is null)
        {
            throw new InvalidOperationException($"Site {siteId.Value} was not found while suspending it for a test.");
        }

        if (until is { } value)
        {
            site.Suspend(value, DateTimeOffset.UtcNow);
        }
        else
        {
            site.LiftSuspension(DateTimeOffset.UtcNow);
        }

        site.ClearDomainEvents();
        await repository.SaveAsync(site, CancellationToken.None);
    }

    private RedisCache CreateCache() => new(
        fixture.RedisMultiplexer, new ResiliencePipelineBuilder().AddTimeout(TimeSpan.FromSeconds(2)).Build(), NullLogger<RedisCache>.Instance);

    /// <summary>The identical direct-invocation shape <see cref="OriginAuthorizationTests.InvokeVisitorSessionAsync"/>
    /// uses for the same endpoint - not shared, the same "each integration file owns its own small
    /// plumbing" precedent that file's own remarks (and the wire-test files) already establish.</summary>
    private async Task<int> InvokeVisitorSessionAsync(string publicKey)
    {
        var getSite = new GetSiteConfigByPublicKeyHandler(new SiteRepository(fixture.CreateDbContext()), CreateCache());
        var suspensions = new SiteSuspensionReadStore(fixture.DataSource);
        var tokens = new JwtTokenService(TestSigningKeys.Ring(), "test-issuer", new SystemClock());
        var rateLimiter = new AlwaysAllowRateLimiter();
        var rateLimitOptions = Options.Create(new VisitorSessionRateLimitOptions());

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(Options.Create(new Microsoft.AspNetCore.Http.Json.JsonOptions()));
        var httpContext = new DefaultHttpContext
        {
            RequestServices = services.BuildServiceProvider(),
            Response = { Body = new MemoryStream() },
        };

        var result = await AuthEndpoints.HandleVisitorSessionAsync(
            new AuthEndpoints.VisitorSessionRequest(publicKey),
            getSite, new SiteInstallationSignalRepository(fixture.DataSource), new EnabledModuleReadStore(fixture.DataSource), suspensions,
            rateLimiter, rateLimitOptions, new UuidV7Generator(), new SystemClock(), tokens, httpContext, CancellationToken.None);
        await result.ExecuteAsync(httpContext);
        return httpContext.Response.StatusCode;
    }

    private sealed class AlwaysAllowRateLimiter : Ago.Platform.Abstractions.IRateLimiter
    {
        public Task<Ago.Platform.Abstractions.RateLimitDecision> CheckAsync(
            Ago.Platform.Abstractions.RateLimitKey key, Ago.Platform.Abstractions.RateLimitRule rule, CancellationToken cancellationToken) =>
            Task.FromResult(new Ago.Platform.Abstractions.RateLimitDecision(true, TimeSpan.Zero));
    }
}
