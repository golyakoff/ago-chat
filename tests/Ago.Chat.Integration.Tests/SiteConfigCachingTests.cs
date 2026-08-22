using Ago.Chat.Application.Abstractions;
using Ago.Chat.Application.UseCases.GetSiteByPublicKey;
using Ago.Chat.Domain;
using Ago.Chat.Infrastructure.Postgres;
using Ago.Platform.Abstractions;
using Ago.Platform.Caching.Redis;
using Microsoft.Extensions.Logging.Abstractions;
using Polly;

namespace Ago.Chat.Integration.Tests;

/// <summary>
/// `3-04`'s Done-when: real Redis and real Postgres (Testcontainers, no mocking - testing.md). A
/// counting decorator around the real <see cref="SiteRepository"/> is how "never touches Postgres"
/// gets asserted rather than assumed from timing, per the backlog item's own wording.
/// </summary>
[Collection(SiteCachingCollection.Name)]
public sealed class SiteConfigCachingTests(SiteCachingFixture fixture)
{
    [Fact]
    public async Task HandleAsync_OnACacheMiss_PopulatesFromPostgres_AndASubsequentReadNeverTouchesItAgain()
    {
        var siteId = new SiteId(Guid.NewGuid());
        var publicKey = $"site_{siteId.Value:N}";
        await using (var db = fixture.CreateDbContext())
        {
            db.Sites.Add(new Site(siteId, publicKey, ["https://example.com"]));
            await db.SaveChangesAsync();
        }

        var sites = new CountingSiteRepository(new SiteRepository(fixture.CreateDbContext()));
        var cache = CreateCache();
        var handler = new GetSiteConfigByPublicKeyHandler(sites, cache);

        var first = await handler.HandleAsync(new GetSiteConfigByPublicKey(publicKey), CancellationToken.None);
        Assert.NotNull(first);
        Assert.Equal(siteId.Value, first.SiteId);
        Assert.Equal(1, sites.Calls);

        var second = await handler.HandleAsync(new GetSiteConfigByPublicKey(publicKey), CancellationToken.None);

        Assert.NotNull(second);
        Assert.Equal(siteId.Value, second.SiteId);
        Assert.Equal(1, sites.Calls); // the second read came from Redis, not Postgres
    }

    [Fact]
    public async Task HandleAsync_ForAMissingSite_NegativeCaches_AndASubsequentReadNeverTouchesPostgresAgain()
    {
        var sites = new CountingSiteRepository(new SiteRepository(fixture.CreateDbContext()));
        var cache = CreateCache();
        var handler = new GetSiteConfigByPublicKeyHandler(sites, cache);
        var publicKey = $"no_such_site_{Guid.NewGuid():N}";

        var first = await handler.HandleAsync(new GetSiteConfigByPublicKey(publicKey), CancellationToken.None);
        var second = await handler.HandleAsync(new GetSiteConfigByPublicKey(publicKey), CancellationToken.None);

        Assert.Null(first);
        Assert.Null(second);
        Assert.Equal(1, sites.Calls);
    }

    private RedisCache CreateCache() => new(
        fixture.RedisMultiplexer,
        new ResiliencePipelineBuilder().AddTimeout(TimeSpan.FromSeconds(2)).Build(),
        NullLogger<RedisCache>.Instance);

    private sealed class CountingSiteRepository(ISiteRepository inner) : ISiteRepository
    {
        public int Calls { get; private set; }

        public Task<Site?> GetByPublicKeyAsync(string publicKey, CancellationToken cancellationToken)
        {
            Calls++;
            return inner.GetByPublicKeyAsync(publicKey, cancellationToken);
        }

        public Task<Site?> GetByIdAsync(SiteId id, CancellationToken cancellationToken)
        {
            Calls++;
            return inner.GetByIdAsync(id, cancellationToken);
        }

        public Task<bool> AnyAllowsOriginAsync(string origin, CancellationToken cancellationToken)
        {
            Calls++;
            return inner.AnyAllowsOriginAsync(origin, cancellationToken);
        }
    }
}
