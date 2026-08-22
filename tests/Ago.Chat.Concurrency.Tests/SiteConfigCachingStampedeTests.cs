using Ago.Chat.Application.Abstractions;
using Ago.Chat.Application.UseCases.GetSiteByPublicKey;
using Ago.Chat.Domain;
using Ago.Chat.Infrastructure.Postgres;
using Ago.Platform.Abstractions;
using Ago.Platform.Caching.Redis;
using Microsoft.Extensions.Logging.Abstractions;
using Polly;

namespace Ago.Chat.Concurrency.Tests;

/// <summary>testing.md's stampede-protection concurrency claim, proven under actual concurrency
/// rather than asserted: N readers hitting a cold key at once must all get the correct value while
/// the backing store (Postgres, via <see cref="SiteRepository"/>) is hit exactly once - the same
/// shape 2-05/2-06 use for their own concurrency claims.</summary>
[Collection(SiteCachingConcurrencyCollection.Name)]
public sealed class SiteConfigCachingStampedeTests(SiteCachingConcurrencyFixture fixture)
{
    [Fact]
    public async Task ManyConcurrentReaders_AgainstAColdKey_AllGetTheCorrectValue_AndThePostgresRepositoryIsHitOnce()
    {
        var siteId = new SiteId(Guid.NewGuid());
        var publicKey = $"site_{siteId.Value:N}";
        await using (var db = fixture.CreateDbContext())
        {
            db.Sites.Add(new Site(siteId, publicKey, ["https://example.com"]));
            await db.SaveChangesAsync();
        }

        var sites = new CountingSiteRepository(fixture);
        var cache = new RedisCache(
            fixture.RedisMultiplexer, new ResiliencePipelineBuilder().AddTimeout(TimeSpan.FromSeconds(2)).Build(), NullLogger<RedisCache>.Instance);
        var handler = new GetSiteConfigByPublicKeyHandler(sites, cache);

        var results = await Task.WhenAll(Enumerable.Range(0, 30)
            .Select(_ => handler.HandleAsync(new GetSiteConfigByPublicKey(publicKey), CancellationToken.None)));

        Assert.All(results, r => Assert.NotNull(r));
        Assert.All(results, r => Assert.Equal(siteId.Value, r!.SiteId));
        Assert.Equal(1, sites.Calls);
    }

    private sealed class CountingSiteRepository(SiteCachingConcurrencyFixture fixture) : ISiteRepository
    {
        private int _calls;

        public int Calls => _calls;

        public async Task<Site?> GetByPublicKeyAsync(string publicKey, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _calls);
            // A brief, deliberate delay so concurrent callers genuinely overlap in the cold-key
            // window instead of racing to finish before the next one even starts (matching
            // RedisCacheTests' own reasoning for its in-process stampede test).
            await Task.Delay(200, cancellationToken);
            await using var db = fixture.CreateDbContext();
            return await new SiteRepository(db).GetByPublicKeyAsync(publicKey, cancellationToken);
        }

        public async Task<Site?> GetByIdAsync(SiteId id, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _calls);
            await using var db = fixture.CreateDbContext();
            return await new SiteRepository(db).GetByIdAsync(id, cancellationToken);
        }

        public async Task<bool> AnyAllowsOriginAsync(string origin, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _calls);
            await using var db = fixture.CreateDbContext();
            return await new SiteRepository(db).AnyAllowsOriginAsync(origin, cancellationToken);
        }
    }
}
