using System.Net.Http.Headers;
using System.Text;
using Ago.Chat.Infrastructure.Postgres.Persistence;
using Ago.Platform.Messaging.RabbitMq;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using Testcontainers.PostgreSql;
using Testcontainers.RabbitMq;

namespace Ago.Chat.Integration.Tests;

/// <summary>
/// `6-05`'s own containers - Postgres (`webhook_endpoints`/`webhook_deliveries`, the idempotency
/// ledger) and RabbitMQ (the two new consumers). No Redis: unlike `ConnectionFanoutFixture`, nothing
/// this dispatcher does touches the connection registry or the cache - it never holds a realtime
/// connection and never reads site config, so paying to start a Redis container here would be pure
/// waste (the same "extend the fixture that actually needs the extra resource, do not default every
/// fixture to every resource" reasoning `ConnectionFanoutFixture`'s own remarks give in the other
/// direction).
/// </summary>
public sealed class WebhookDispatchFixture : IAsyncLifetime
{
    private const string RabbitMqUsername = "ago-test";
    private const string RabbitMqPassword = "ago-test-local-dev";

    // `15-17`: the RabbitMQ management API port - see `ConnectionFanoutFixture`'s own remarks on
    // this same constant. Needed here too, now that `WebhookDispatchSharedQueueRegressionTests`/
    // `WebhookDispatchIdempotencyTests`' own subscription wait checks a queue's live `consumers`
    // count rather than merely whether the queue exists (`RabbitMqSubscriptionTestHelpers`'s own
    // remarks on why a passive AMQP declare alone was not enough).
    private const int RabbitMqManagementPort = 15672;

    private PostgreSqlContainer _postgres = null!;
    private RabbitMqContainer _rabbitMq = null!;
    private IDisposable _dockerLock = null!;

    public NpgsqlDataSource DataSource { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        _dockerLock = await DockerResourceLock.AcquireAsync();

        _postgres = new PostgreSqlBuilder("postgres:17-alpine").Build();
        _rabbitMq = new RabbitMqBuilder("rabbitmq:4-management")
            .WithUsername(RabbitMqUsername).WithPassword(RabbitMqPassword)
            .WithPortBinding(RabbitMqManagementPort, true)
            .Build();

        await Task.WhenAll(_postgres.StartAsync(), _rabbitMq.StartAsync());

        DataSource = new NpgsqlDataSourceBuilder(_postgres.GetConnectionString()).Build();

        await using var db = CreateDbContext();
        await db.Database.MigrateAsync();
    }

    public async Task DisposeAsync()
    {
        await DataSource.DisposeAsync();
        await Task.WhenAll(_postgres.DisposeAsync().AsTask(), _rabbitMq.DisposeAsync().AsTask());
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
    }), NullLogger<RabbitMqConnection>.Instance);

    /// <summary>`15-17`: a client against this container's own RabbitMQ management API - see
    /// `ConnectionFanoutFixture.CreateRabbitMqManagementClient`'s own remarks (identical shape, same
    /// credentials over HTTP as <see cref="CreateRabbitMqConnection"/> already uses over AMQP).
    /// </summary>
    public HttpClient CreateRabbitMqManagementClient()
    {
        var client = new HttpClient
        {
            BaseAddress = new Uri($"http://{_rabbitMq.Hostname}:{_rabbitMq.GetMappedPublicPort(RabbitMqManagementPort)}"),
        };
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{RabbitMqUsername}:{RabbitMqPassword}")));
        return client;
    }
}

[CollectionDefinition(Name)]
public sealed class WebhookDispatchCollection : ICollectionFixture<WebhookDispatchFixture>
{
    public const string Name = "WebhookDispatch";
}
