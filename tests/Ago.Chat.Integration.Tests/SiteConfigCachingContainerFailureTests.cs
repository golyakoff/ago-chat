using Ago.Chat.Application.Abstractions;
using Ago.Chat.Application.UseCases.GetSiteByPublicKey;
using Ago.Chat.Domain;
using Ago.Chat.Infrastructure.Postgres;
using Ago.Chat.Infrastructure.Postgres.Persistence;
using Ago.Platform.Caching.Redis;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Polly;
using StackExchange.Redis;
using Testcontainers.PostgreSql;
using Testcontainers.Redis;

namespace Ago.Chat.Integration.Tests;

/// <summary>`adr/0009`'s "FLUSHALL is survivable" claim at the product level: a Redis container
/// stopped mid-test must not turn the widget handshake's site lookup into a 500 - it degrades to a
/// slower, correct read straight from Postgres (`resilience.md`'s Redis row), matching
/// `Ago.Platform.Integration.Tests.RedisCacheContainerFailureTests`' shape one layer up. Its own,
/// non-shared containers so stopping Redis mid-test cannot affect any other test.</summary>
public sealed class SiteConfigCachingContainerFailureTests
{
    [Fact]
    public async Task ReadsAgainstAStoppedRedis_StillSucceed_AsSlowerCorrectReadsFromPostgres()
    {
        var postgres = new PostgreSqlBuilder("postgres:17-alpine").Build();
        var redis = new RedisBuilder("redis:7-alpine").Build();
        await Task.WhenAll(postgres.StartAsync(), redis.StartAsync());

        await using var dataSource = new NpgsqlDataSourceBuilder(postgres.GetConnectionString()).Build();
        await using (var migrate = CreateDbContext(dataSource))
        {
            await migrate.Database.MigrateAsync();
        }

        var siteId = new SiteId(Guid.NewGuid());
        var publicKey = $"site_{siteId.Value:N}";
        await using (var db = CreateDbContext(dataSource))
        {
            db.Sites.Add(new Site(siteId, publicKey, []));
            await db.SaveChangesAsync();
        }

        var redisConfiguration = ConfigurationOptions.Parse(redis.GetConnectionString());
        redisConfiguration.ConnectTimeout = 2000;
        redisConfiguration.SyncTimeout = 2000;
        redisConfiguration.AbortOnConnectFail = false;
        await using var multiplexer = await ConnectionMultiplexer.ConnectAsync(redisConfiguration);
        var cache = new RedisCache(
            multiplexer, new ResiliencePipelineBuilder().AddTimeout(TimeSpan.FromMilliseconds(500)).Build(), NullLogger<RedisCache>.Instance);

        ISiteRepository sites = new SiteRepository(CreateDbContext(dataSource));
        var handler = new GetSiteConfigByPublicKeyHandler(sites, cache);

        // Warm up while Redis is still up - matching RedisCacheContainerFailureTests' own reasoning,
        // a cache that only later finds Redis gone is the interesting case.
        var warm = await handler.HandleAsync(new GetSiteConfigByPublicKey(publicKey), CancellationToken.None);
        Assert.NotNull(warm);

        await redis.StopAsync();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        SiteConfigDto? result = null;
        var exception = await Record.ExceptionAsync(async () =>
        {
            result = await handler.HandleAsync(new GetSiteConfigByPublicKey(publicKey), cts.Token);
        });

        Assert.Null(exception);
        Assert.False(cts.IsCancellationRequested, "Should have returned on its own, not because the test's timeout fired.");
        Assert.NotNull(result);
        Assert.Equal(siteId.Value, result.SiteId);

        await postgres.DisposeAsync();
        await redis.DisposeAsync();
    }

    private static AgoChatDbContext CreateDbContext(NpgsqlDataSource dataSource) =>
        new(new DbContextOptionsBuilder<AgoChatDbContext>().UseNpgsql(dataSource).Options);
}
