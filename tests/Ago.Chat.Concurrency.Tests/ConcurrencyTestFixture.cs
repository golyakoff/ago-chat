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
        // `6-10`: a deliberately hostile server, not the defaults. `deadlock_timeout=10ms` (against
        // the 1 s default) does not create deadlocks - it only makes Postgres go looking for a cycle
        // sooner, so a real one this suite's contention produces is found and reported on this run
        // instead of on a loaded CI runner three merges later. `log_lock_waits=on` puts the wait
        // queues and the full deadlock graph in the container's own log, which
        // CloseConversationCapacityConcurrencyTests reads back through GetPostgresLogsAsync to prove
        // its storm was hostile enough to mean anything.
        _postgres = new PostgreSqlBuilder("postgres:17-alpine")
            .WithCommand("-c", "deadlock_timeout=10ms", "-c", "log_lock_waits=on")
            .Build();
        RabbitMq = new RabbitMqBuilder("rabbitmq:4-management").WithUsername(Username).WithPassword(Password).Build();
        await Task.WhenAll(_postgres.StartAsync(), RabbitMq.StartAsync());

        // Kept separately from DataSource: NpgsqlDataSource.ConnectionString redacts the password
        // (it is meant for logging/display), so building a second DataSource - or a DbContext -
        // from that redacted string fails SASL auth. CreateServiceProvider() needs the real one.
        // `Include Error Detail` so a Postgres error that carries a DETAIL line actually prints it.
        // Without it, Npgsql replaces the detail with "Detail redacted as it may contain sensitive
        // data", which is the right default for a production connection string and precisely the
        // wrong one here: a `40P01 deadlock detected` puts the entire deadlock graph - which two
        // transactions, which relations, which statements - in that DETAIL line and nowhere else.
        // A deadlock this suite caught on CI (2026-08-25, twice, never reproducible locally) was
        // unactionable for exactly that reason. This is a throwaway container seeded only by these
        // tests, so there is no sensitive data for the flag to expose.
        _connectionString = new NpgsqlConnectionStringBuilder(_postgres.GetConnectionString())
        {
            IncludeErrorDetail = true,
        }.ConnectionString;
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

    /// <summary>`6-10`: the Postgres server's own stderr, deadlock reports and lock waits included.
    /// A test that produces a deadlock and then discards the server's explanation of it costs another
    /// full cycle to learn nothing - which is exactly what the two CI failures on 2026-08-25 cost.</summary>
    public async Task<string> GetPostgresLogsAsync()
    {
        var (stdout, stderr) = await _postgres.GetLogsAsync();
        return stdout + Environment.NewLine + stderr;
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
