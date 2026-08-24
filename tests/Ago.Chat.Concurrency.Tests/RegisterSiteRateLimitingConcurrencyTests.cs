using Ago.Chat.Application.UseCases.RegisterSite;
using Ago.Chat.Infrastructure.Postgres;
using Ago.Platform.Caching.Redis;
using Ago.Platform.Hosting;
using Ago.Platform.Kernel;
using Microsoft.Extensions.Logging.Abstractions;
using Polly;

namespace Ago.Chat.Concurrency.Tests;

/// <summary>`10-02`'s own Done-when: "Rate limiting proven the same real-concurrency way `3-05`'s own
/// tests prove their bucket (N concurrent calls, exactly the configured capacity honoured, not a
/// sequential loop)" - mirrors <see cref="RateLimitingConcurrencyTests"/> exactly, against
/// `RegisterSiteHandler`'s per-IP bucket instead of `SendVisitorMessageHandler`'s per-visitor one.
/// Reuses <see cref="SiteCachingConcurrencyFixture"/> (Postgres + Redis) rather than a third
/// near-identical fixture, the same reuse <see cref="RateLimitingConcurrencyTests"/> already applies.
///
/// Each concurrent call uses a distinct `sub` (a real registration attempt from a different identity)
/// but the same caller IP - proving the *IP* bucket specifically is what limits a burst of otherwise
/// legitimate-looking registrations, the abuse surface `10-01`'s own Scope names as the real one this
/// project's code must guard. The per-subject bucket is generous enough (`RegisterSiteRateLimitOptions`'
/// own defaults) that it never trips first in this scenario.</summary>
[Collection(SiteCachingConcurrencyCollection.Name)]
public sealed class RegisterSiteRateLimitingConcurrencyTests(SiteCachingConcurrencyFixture fixture)
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task ManyConcurrentRegistrations_FromTheSameIp_AllowExactlyCapacityAndDenyTheRest()
    {
        const string requestIp = "203.0.113.9";
        var externalSubjectIds = Enumerable.Range(0, 30).Select(i => $"concurrency-sub-{i}-{Guid.NewGuid():N}").ToList();

        var limiter = new RedisRateLimiter(
            fixture.RedisMultiplexer, new ResiliencePipelineBuilder().AddTimeout(TimeSpan.FromSeconds(2)).Build(), NullLogger<RedisRateLimiter>.Instance);
        // Capacity 5 on the IP bucket, refill slow enough that none of it refills meaningfully during
        // the burst below; the subject bucket is deliberately generous so it never denies first.
        var options = new RegisterSiteRateLimitOptions
        {
            PerSubjectCapacity = 1000,
            PerSubjectRefillPerSecond = 1000,
            PerIpCapacity = 5,
            PerIpRefillPerSecond = 0.001,
        };

        var results = await Task.WhenAll(externalSubjectIds.Select(async externalSubjectId =>
        {
            await using var db = fixture.CreateDbContext();
            var handler = new RegisterSiteHandler(
                new OperatorRepository(db),
                new SiteRegistrationRepository(db),
                limiter,
                options,
                new UuidV7Generator(),
                new SystemClock());
            return await handler.HandleAsync(
                new RegisterSite(externalSubjectId, requestIp, "Acme Support", "https://shop.example.com"),
                CancellationToken.None);
        }));

        Assert.Equal(5, results.Count(r => r.IsSuccess));
        var denied = results.Where(r => r.IsFailure).ToList();
        Assert.Equal(25, denied.Count);
        Assert.All(denied, r => Assert.Equal("Site.RateLimited", r.Error!.Value.Code));
    }
}
