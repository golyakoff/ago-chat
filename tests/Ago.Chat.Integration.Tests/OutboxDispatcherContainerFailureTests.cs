using System.Collections.Concurrent;
using Ago.Chat.Infrastructure.Postgres.Persistence;
using Ago.Chat.Worker;
using Ago.Platform.Abstractions;
using Ago.Platform.Hosting;
using Ago.Platform.Messaging.RabbitMq;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using Testcontainers.PostgreSql;
using Testcontainers.RabbitMq;

namespace Ago.Chat.Integration.Tests;

/// <summary>Roadmap's literal Stage 2 "done when": kill the dispatcher's broker mid-batch, restart
/// it, zero acknowledged-but-lost messages, zero duplicates. Own, non-shared containers - pausing
/// the broker here must not disturb <see cref="OutboxDispatcherFixture"/>'s tests.</summary>
public sealed class OutboxDispatcherContainerFailureTests
{
    [Fact]
    public async Task PausingAndUnpausingRabbitMq_EveryRowEventuallyPublishes_NoDuplicates()
    {
        var postgres = new PostgreSqlBuilder("postgres:17-alpine").Build();
        var rabbitMq = new RabbitMqBuilder("rabbitmq:4-management").WithUsername("ago-test").WithPassword("ago-test-local-dev").Build();
        await Task.WhenAll(postgres.StartAsync(), rabbitMq.StartAsync());

        await using var dataSource = new NpgsqlDataSourceBuilder(postgres.GetConnectionString()).Build();
        var dbOptions = new DbContextOptionsBuilder<AgoChatDbContext>().UseNpgsql(dataSource).Options;
        await using (var migrate = new AgoChatDbContext(dbOptions))
        {
            await migrate.Database.MigrateAsync();
        }

        var rabbitOptions = Options.Create(new RabbitMqOptions
        {
            HostName = rabbitMq.Hostname,
            Port = rabbitMq.GetMappedPublicPort(5672),
            UserName = "ago-test",
            Password = "ago-test-local-dev",
        });

        await using var consumerConnection = new RabbitMqConnection(rabbitOptions);
        var consumer = new RabbitMqEventConsumer(consumerConnection);
        var receivedIds = new ConcurrentBag<Guid>();
        await consumer.SubscribeAsync(
            "MessageAccepted", SubscriptionMode.Broadcast, "test-consumer", new RetryPolicy(3, TimeSpan.FromMilliseconds(200), $"dlq.{Guid.NewGuid():N}"),
            (envelope, ctx, ct) => { receivedIds.Add(envelope.MessageId); return ctx.AckAsync(ct); }, CancellationToken.None);

        await using var dispatcherConnection = new RabbitMqConnection(rabbitOptions);
        var dispatcher = new OutboxDispatcher(
            dataSource, new RabbitMqEventPublisher(dispatcherConnection), new SystemClock(),
            // Rows in a claimed batch are published sequentially, each against its own
            // PublishTimeout - with the default 10s, a batch of 5 stuck against a paused broker takes
            // up to 50s to give up on. 2s keeps that worst case at 10s, so this test's own wait
            // budget is dominated by real recovery time, not by how long the dispatcher takes to
            // notice the row it was already working on is stuck.
            Options.Create(new OutboxDispatcherOptions
            {
                PollInterval = TimeSpan.FromSeconds(1),
                BatchSize = 20,
                PublishTimeout = TimeSpan.FromSeconds(2),
            }),
            NullLogger<OutboxDispatcher>.Instance);

        await dispatcher.StartAsync(CancellationToken.None);
        try
        {
            var firstBatch = await SeedRowsAsync(dbOptions, 5);
            await OutboxTestHelpers.WaitUntilAsync(() => AllPublishedAsync(dbOptions, firstBatch), TimeSpan.FromSeconds(15));
            Assert.True(await AllPublishedAsync(dbOptions, firstBatch), "Baseline batch should publish while the broker is up.");

            // Pause, not stop: docker pause freezes the container's processes without tearing down
            // its network/port mapping - Testcontainers reassigns a new random host port on a real
            // stop+start (verified empirically), which a stable production RabbitMQ Service address
            // never does. Pause is what actually matches "the broker is briefly unreachable" without
            // also faking a DNS/address change no real deployment would have.
            await rabbitMq.PauseAsync();

            var secondBatch = await SeedRowsAsync(dbOptions, 5);
            // Give the dispatcher a few cycles to try, fail, and increment attempts against an
            // unreachable broker.
            await Task.Delay(TimeSpan.FromSeconds(4));
            Assert.False(await AllPublishedAsync(dbOptions, secondBatch), "Nothing should publish while the broker is unreachable.");

            await rabbitMq.UnpauseAsync();

            // This test used to fail unpredictably (sometimes 39s, sometimes never inside 90s+) for a
            // reason that had nothing to do with RabbitMQ recovery timing: OutboxDispatcher's poll
            // loop raced a shared PeriodicTimer's WaitForNextTickAsync against the LISTEN/NOTIFY wake
            // signal inside Task.WhenAny. WaitForNextTickAsync allows only one in-flight call - the
            // moment a notification won the race once (as one always did here, from the baseline
            // batch's own inserts), the abandoned timer call permanently broke the next iteration's
            // call to the same timer, and the dispatcher silently stopped claiming batches forever -
            // confirmed by adding direct file-based tracing (console/ILogger output through `dotnet
            // test` was not trustworthy for this: no new lines appeared for 40+ seconds while the
            // process was demonstrably still alive). Fixed in OutboxDispatcher.ExecuteAsync by
            // replacing the shared PeriodicTimer with a fresh Task.Delay per iteration, which has no
            // such restriction. With that fixed, five repeated runs landed at 39-60s; 60s keeps a
            // comfortable margin without the artificial padding the old, wrong theory motivated.
            await OutboxTestHelpers.WaitUntilAsync(() => AllPublishedAsync(dbOptions, secondBatch), TimeSpan.FromSeconds(60));
            Assert.True(await AllPublishedAsync(dbOptions, secondBatch), "The second batch should publish once the broker returns.");

            var allIds = firstBatch.Concat(secondBatch).ToList();
            await OutboxTestHelpers.WaitUntilAsync(() => receivedIds.Count >= allIds.Count, TimeSpan.FromSeconds(10));

            Assert.Equal(allIds.Count, receivedIds.Count);
            Assert.Equal(allIds.OrderBy(x => x), receivedIds.Distinct().OrderBy(x => x));
        }
        finally
        {
            await dispatcher.StopAsync(CancellationToken.None);
            await postgres.DisposeAsync();
            await rabbitMq.DisposeAsync();
        }
    }

    private static async Task<IReadOnlyList<Guid>> SeedRowsAsync(DbContextOptions<AgoChatDbContext> dbOptions, int count)
    {
        await using var db = new AgoChatDbContext(dbOptions);
        var ids = new List<Guid>();
        for (var i = 0; i < count; i++)
        {
            var id = Guid.NewGuid();
            ids.Add(id);
            db.Add(new Ago.Platform.Persistence.Postgres.OutboxMessage(
                id, DateTimeOffset.UtcNow, "MessageAccepted", 1, "{}", $"key-{id:N}", Guid.NewGuid()));
        }

        await db.SaveChangesAsync(CancellationToken.None);
        return ids;
    }

    private static async Task<bool> AllPublishedAsync(DbContextOptions<AgoChatDbContext> dbOptions, IReadOnlyList<Guid> ids)
    {
        await using var db = new AgoChatDbContext(dbOptions);
        var count = await db.Set<Ago.Platform.Persistence.Postgres.OutboxMessage>()
            .CountAsync(o => ids.Contains(o.Id) && o.PublishedAt != null, CancellationToken.None);
        return count == ids.Count;
    }
}
