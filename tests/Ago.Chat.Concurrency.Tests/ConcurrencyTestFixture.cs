using Ago.Chat.Infrastructure.Postgres;
using Ago.Chat.Infrastructure.Postgres.Persistence;
using Ago.Platform.Hosting;
using Ago.Platform.Messaging.RabbitMq;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Npgsql;
using Testcontainers.PostgreSql;
using Testcontainers.RabbitMq;

namespace Ago.Chat.Concurrency.Tests;

/// <summary>2-05: one Postgres and one RabbitMQ container per test class collection - both real,
/// per testing.md's "never mock the database" extended to "never mock the broker either" for
/// guarantees (idempotency, crash-then-redeliver) that only hold against the real thing.</summary>
public sealed class ConcurrencyTestFixture : IAsyncLifetime
{
    private const string Username = "ago-test";
    private const string Password = "ago-test-local-dev";

    private PostgreSqlContainer _postgres = null!;
    private string _connectionString = null!;

    public RabbitMqContainer RabbitMq { get; private set; } = null!;

    public NpgsqlDataSource DataSource { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        _postgres = new PostgreSqlBuilder("postgres:17-alpine").Build();
        RabbitMq = new RabbitMqBuilder("rabbitmq:4-management").WithUsername(Username).WithPassword(Password).Build();
        await Task.WhenAll(_postgres.StartAsync(), RabbitMq.StartAsync());

        // Kept separately from DataSource: NpgsqlDataSource.ConnectionString redacts the password
        // (it is meant for logging/display), so building a second DataSource - or a DbContext -
        // from that redacted string fails SASL auth. CreateServiceProvider() needs the real one.
        _connectionString = _postgres.GetConnectionString();
        DataSource = new NpgsqlDataSourceBuilder(_connectionString).Build();

        await using var db = CreateDbContext();
        await db.Database.MigrateAsync();
    }

    public async Task DisposeAsync()
    {
        await DataSource.DisposeAsync();
        await _postgres.DisposeAsync();
        await RabbitMq.DisposeAsync();
    }

    public AgoChatDbContext CreateDbContext() =>
        new(new DbContextOptionsBuilder<AgoChatDbContext>().UseNpgsql(DataSource).Options);

    public RabbitMqOptions BuildRabbitMqOptions() => new()
    {
        HostName = RabbitMq.Hostname,
        Port = RabbitMq.GetMappedPublicPort(5672),
        UserName = Username,
        Password = Password,
    };

    /// <summary>A fresh DI container wired the same way <c>ChatModule</c> wires production - just
    /// not <em>through</em> ChatModule, since it reads its connection string from an environment
    /// variable rather than accepting one as a parameter (that env-var read is Program.cs's job, not
    /// something a test should have to mutate process-wide state to satisfy). Scoped registrations
    /// (`IConversationRepository`, `IInboxChecker`) mean each `IServiceScopeFactory.CreateScope()`
    /// call - exactly what `UnreadCounterConsumer` does per message - gets its own `AgoChatDbContext`
    /// and thus its own transaction, matching production.</summary>
    public ServiceProvider CreateServiceProvider()
    {
        var services = new ServiceCollection();
        services.AddPostgresPersistence(_connectionString);
        services.AddPlatformKernel();
        services.AddScoped<Ago.Chat.Application.UseCases.RecordUnread.RecordUnreadMessageHandler>();
        return services.BuildServiceProvider();
    }
}

[CollectionDefinition(Name)]
public sealed class ConcurrencyCollection : ICollectionFixture<ConcurrencyTestFixture>
{
    public const string Name = "Concurrency";
}
