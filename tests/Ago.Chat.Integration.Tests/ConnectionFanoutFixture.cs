using Ago.Chat.Infrastructure.Postgres.Persistence;
using Ago.Platform.Messaging.RabbitMq;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Npgsql;
using StackExchange.Redis;
using Testcontainers.PostgreSql;
using Testcontainers.RabbitMq;
using Testcontainers.Redis;

namespace Ago.Chat.Integration.Tests;

/// <summary>3-02's end-to-end proof spans three external resources - Postgres (the message and its
/// outbox row), RabbitMQ (MessageAccepted and the per-node deliveries), Redis (the connection
/// registry) - so this combines all three container lifecycles, the same reasoning as
/// <c>OutboxDispatcherFixture</c> extended by one more resource rather than folded into it: existing
/// tests on that fixture do not need Redis, and should not pay for starting it.</summary>
public sealed class ConnectionFanoutFixture : IAsyncLifetime
{
    private const string RabbitMqUsername = "ago-test";
    private const string RabbitMqPassword = "ago-test-local-dev";

    private PostgreSqlContainer _postgres = null!;
    private RabbitMqContainer _rabbitMq = null!;
    private RedisContainer _redis = null!;
    private IDisposable _dockerLock = null!;

    public NpgsqlDataSource DataSource { get; private set; } = null!;

    public IConnectionMultiplexer RedisMultiplexer { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        _dockerLock = await DockerResourceLock.AcquireAsync();

        _postgres = new PostgreSqlBuilder("postgres:17-alpine").Build();
        _rabbitMq = new RabbitMqBuilder("rabbitmq:4-management").WithUsername(RabbitMqUsername).WithPassword(RabbitMqPassword).Build();
        _redis = new RedisBuilder("redis:7-alpine").Build();

        await Task.WhenAll(_postgres.StartAsync(), _rabbitMq.StartAsync(), _redis.StartAsync());

        DataSource = new NpgsqlDataSourceBuilder(_postgres.GetConnectionString()).Build();
        RedisMultiplexer = await ConnectionMultiplexer.ConnectAsync(_redis.GetConnectionString());

        await using var db = CreateDbContext();
        await db.Database.MigrateAsync();
    }

    public async Task DisposeAsync()
    {
        await DataSource.DisposeAsync();
        await RedisMultiplexer.DisposeAsync();
        await Task.WhenAll(_postgres.DisposeAsync().AsTask(), _rabbitMq.DisposeAsync().AsTask(), _redis.DisposeAsync().AsTask());
        _dockerLock.Dispose();
    }

    public AgoChatDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<AgoChatDbContext>().UseNpgsql(DataSource).Options;
        return new AgoChatDbContext(options);
    }

    public RabbitMqConnection CreateRabbitMqConnection() => new(Options.Create(new RabbitMqOptions
    {
        HostName = _rabbitMq.Hostname,
        Port = _rabbitMq.GetMappedPublicPort(5672),
        UserName = RabbitMqUsername,
        Password = RabbitMqPassword,
    }));
}

[CollectionDefinition(Name)]
public sealed class ConnectionFanoutCollection : ICollectionFixture<ConnectionFanoutFixture>
{
    public const string Name = "ConnectionFanout";
}
