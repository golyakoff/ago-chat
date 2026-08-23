using Ago.Chat.Infrastructure.Postgres.Persistence;
using Ago.Platform.Messaging.RabbitMq;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Npgsql;
using Testcontainers.PostgreSql;
using Testcontainers.RabbitMq;

namespace Ago.Chat.Integration.Tests;

/// <summary>2-04: one Postgres (migrated, so the NOTIFY trigger exists) and one RabbitMQ container
/// per test class collection - both real, per testing.md's "never mock the database" extended to
/// "never mock the broker either" for a guarantee the dispatcher itself provides.</summary>
public sealed class OutboxDispatcherFixture : IAsyncLifetime
{
    private const string Username = "ago-test";
    private const string Password = "ago-test-local-dev";

    private PostgreSqlContainer _postgres = null!;
    private IDisposable _dockerLock = null!;

    public RabbitMqContainer RabbitMq { get; private set; } = null!;

    public NpgsqlDataSource DataSource { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        _dockerLock = await DockerResourceLock.AcquireAsync();

        _postgres = new PostgreSqlBuilder("postgres:17-alpine").Build();
        RabbitMq = new RabbitMqBuilder("rabbitmq:4-management").WithUsername(Username).WithPassword(Password).Build();
        await Task.WhenAll(_postgres.StartAsync(), RabbitMq.StartAsync());

        DataSource = new NpgsqlDataSourceBuilder(_postgres.GetConnectionString()).Build();

        await using var db = CreateDbContext();
        await db.Database.MigrateAsync();
    }

    public async Task DisposeAsync()
    {
        await DataSource.DisposeAsync();
        await _postgres.DisposeAsync();
        await RabbitMq.DisposeAsync();
        _dockerLock.Dispose();
    }

    public AgoChatDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<AgoChatDbContext>().UseNpgsql(DataSource).Options;
        return new AgoChatDbContext(options);
    }

    public RabbitMqConnection CreateRabbitMqConnection() => new(Options.Create(new RabbitMqOptions
    {
        HostName = RabbitMq.Hostname,
        Port = RabbitMq.GetMappedPublicPort(5672),
        UserName = Username,
        Password = Password,
    }));
}

[CollectionDefinition(Name)]
public sealed class OutboxDispatcherCollection : ICollectionFixture<OutboxDispatcherFixture>
{
    public const string Name = "OutboxDispatcher";
}
