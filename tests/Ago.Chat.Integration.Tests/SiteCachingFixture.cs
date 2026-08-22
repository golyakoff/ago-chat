using Ago.Chat.Infrastructure.Postgres.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using StackExchange.Redis;
using Testcontainers.PostgreSql;
using Testcontainers.Redis;

namespace Ago.Chat.Integration.Tests;

/// <summary>`3-04`'s site-config caching genuinely spans two external resources (the site itself in
/// Postgres, the cache in Redis) - the same reasoning as <c>ConnectionFanoutFixture</c> combining
/// Redis and RabbitMQ for 3-02: one fixture per genuinely-needed resource combination, rather than
/// forcing a test class to carry two <c>[Collection]</c> attributes (xUnit allows only one).</summary>
public sealed class SiteCachingFixture : IAsyncLifetime
{
    private PostgreSqlContainer _postgres = null!;
    private RedisContainer _redis = null!;

    public NpgsqlDataSource DataSource { get; private set; } = null!;

    public IConnectionMultiplexer RedisMultiplexer { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        _postgres = new PostgreSqlBuilder("postgres:17-alpine").Build();
        _redis = new RedisBuilder("redis:7-alpine").Build();
        await Task.WhenAll(_postgres.StartAsync(), _redis.StartAsync());

        DataSource = new NpgsqlDataSourceBuilder(_postgres.GetConnectionString()).Build();
        RedisMultiplexer = await ConnectionMultiplexer.ConnectAsync(_redis.GetConnectionString());

        await using var db = CreateDbContext();
        await db.Database.MigrateAsync();
    }

    public async Task DisposeAsync()
    {
        await DataSource.DisposeAsync();
        await RedisMultiplexer.DisposeAsync();
        await Task.WhenAll(_postgres.DisposeAsync().AsTask(), _redis.DisposeAsync().AsTask());
    }

    public AgoChatDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<AgoChatDbContext>().UseNpgsql(DataSource).Options;
        return new AgoChatDbContext(options);
    }
}

[CollectionDefinition(Name)]
public sealed class SiteCachingCollection : ICollectionFixture<SiteCachingFixture>
{
    public const string Name = "SiteCaching";
}
