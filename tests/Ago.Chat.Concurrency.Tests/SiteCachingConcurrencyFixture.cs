using Ago.Chat.Infrastructure.Postgres.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using StackExchange.Redis;
using Testcontainers.PostgreSql;
using Testcontainers.Redis;

namespace Ago.Chat.Concurrency.Tests;

/// <summary>`3-04`'s stampede-protection claim needs real Postgres (the backing store being hit, or
/// not) and real Redis (the cache genuinely coordinating concurrent readers) - its own fixture,
/// separate from <see cref="ConcurrencyTestFixture"/>, so the outbox/idempotency tests sharing that
/// one do not pay for a Redis container they never touch.</summary>
public sealed class SiteCachingConcurrencyFixture : IAsyncLifetime
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

    public AgoChatDbContext CreateDbContext() =>
        new(new DbContextOptionsBuilder<AgoChatDbContext>().UseNpgsql(DataSource).Options);
}

[CollectionDefinition(Name)]
public sealed class SiteCachingConcurrencyCollection : ICollectionFixture<SiteCachingConcurrencyFixture>
{
    public const string Name = "SiteCachingConcurrency";
}
