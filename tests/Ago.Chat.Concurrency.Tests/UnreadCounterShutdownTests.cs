using Ago.Chat.Application.Mapping;
using Ago.Chat.Domain;
using Ago.Chat.Infrastructure.Postgres;
using Ago.Chat.Infrastructure.Postgres.Persistence;
using Ago.Chat.Worker;
using Ago.Platform.Hosting;
using Ago.Platform.Kernel;
using Ago.Platform.Messaging.RabbitMq;
using Ago.Platform.Persistence.Postgres;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using Testcontainers.PostgreSql;
using Testcontainers.RabbitMq;

namespace Ago.Chat.Concurrency.Tests;

/// <summary>testing.md's "Shutdown" concurrency test: kill a host mid-load, assert zero
/// acknowledged-but-lost messages (and, since this consumer is idempotent, zero double-counts
/// either - a message consumer #1 finished committing but never got to Ack before dying is exactly
/// what redelivers to consumer #2 and must land as a no-op, not a second increment).
///
/// Own, non-shared containers - matching 2-04's OutboxDispatcherContainerFailureTests precedent:
/// this test forcibly kills a connection mid-batch, and sharing infrastructure with
/// UnreadCounterIdempotencyTests risked exactly the cross-test interference that precedent exists
/// to avoid (observed once as this test occasionally timing out only when run immediately after the
/// idempotency test against the same broker, never in isolation).</summary>
public sealed class UnreadCounterShutdownTests
{
    private const string Username = "ago-test";
    private const string Password = "ago-test-local-dev";
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
    private const int MessageCount = 30;

    [Fact]
    public async Task KillingTheConsumerMidBatch_AndRestarting_LeavesNoMessageDroppedOrDoubleCounted()
    {
        var postgres = new PostgreSqlBuilder("postgres:17-alpine").Build();
        var rabbitMq = new RabbitMqBuilder("rabbitmq:4-management").WithUsername(Username).WithPassword(Password).Build();
        await Task.WhenAll(postgres.StartAsync(), rabbitMq.StartAsync());

        var connectionString = postgres.GetConnectionString();
        await using var dataSource = new NpgsqlDataSourceBuilder(connectionString).Build();
        var dbOptions = new DbContextOptionsBuilder<AgoChatDbContext>().UseNpgsql(dataSource).Options;
        await using (var migrate = new AgoChatDbContext(dbOptions))
        {
            await migrate.Database.MigrateAsync();
        }

        var siteId = new SiteId(Guid.NewGuid());
        var visitorId = new VisitorId(Guid.NewGuid());
        var conversationId = new ConversationId(Guid.NewGuid());

        await using (var db = new AgoChatDbContext(dbOptions))
        {
            db.Sites.Add(new Site(siteId, $"site_{siteId.Value:N}", []));
            db.Visitors.Add(new Visitor(visitorId, siteId, Now));
            db.Add(Conversation.Start(conversationId, siteId, visitorId, Now));
            await db.SaveChangesAsync(CancellationToken.None);
        }

        var idGenerator = new UuidV7Generator();
        var messageIds = Enumerable.Range(1, MessageCount).Select(_ => Guid.NewGuid()).ToList();
        var rabbitOptions = Options.Create(new RabbitMqOptions
        {
            HostName = rabbitMq.Hostname,
            Port = rabbitMq.GetMappedPublicPort(5672),
            UserName = Username,
            Password = Password,
        });

        try
        {
            // Consumer #1 starts (and so declares+binds the durable Competing queue) before
            // anything is published - a fanout exchange drops a message published before any queue
            // is bound to it, it does not defer it. Then kill its connection outright rather than
            // calling StopAsync: a graceful stop would wait for in-flight work to finish and Ack,
            // which proves nothing about a real crash. Disposing the connection directly closes the
            // channel with whatever is unacked at that instant still unacked, so RabbitMQ returns it
            // to the queue for consumer #2 - including, plausibly, a message #1 already committed to
            // Postgres but had not yet Acked when the connection died.
            await using (var services1 = BuildServiceProvider(connectionString))
            {
                var connection1 = new RabbitMqConnection(rabbitOptions, NullLogger<RabbitMqConnection>.Instance);
                var consumer1 = new UnreadCounterConsumer(
                    new RabbitMqEventConsumer(connection1),
                    services1.GetRequiredService<IServiceScopeFactory>(),
                    Options.Create(new UnreadCounterConsumerOptions()),
                    NullLogger<UnreadCounterConsumer>.Instance);

                // See UnreadCounterIdempotencyTests' matching comment: a fixed delay here is not a
                // real readiness signal for "the durable queue exists and is bound" - awaiting
                // ExecuteTask is (found live: this test dropped all MessageCount messages on a
                // slower CI runner where the fixed delay was not enough).
                await consumer1.StartAsync(CancellationToken.None);
                await consumer1.ExecuteTask!;

                await using (var publisherConnection = new RabbitMqConnection(rabbitOptions, NullLogger<RabbitMqConnection>.Instance))
                {
                    var publisher = new RabbitMqEventPublisher(publisherConnection, NullLogger<RabbitMqEventPublisher>.Instance);
                    foreach (var (messageId, sequence) in messageIds.Select((id, i) => (id, i + 1)))
                    {
                        var domainEvent = new MessageAdded(
                            new MessageId(messageId), conversationId, siteId, sequence, MessageAuthorKind.Visitor, Now);
                        await publisher.PublishAsync(MessageAcceptedMapper.ToEnvelope(domainEvent, idGenerator), CancellationToken.None);
                    }
                }

                await Task.Delay(TimeSpan.FromMilliseconds(150));
                await connection1.DisposeAsync();
            }

            // Consumer #2: a fresh instance, as a real restarted process would be - picks up
            // whatever consumer #1 left unacked (including any redelivery of already-committed
            // work) and finishes the batch.
            await using var services2 = BuildServiceProvider(connectionString);
            await using var connection2 = new RabbitMqConnection(rabbitOptions, NullLogger<RabbitMqConnection>.Instance);
            var consumer2 = new UnreadCounterConsumer(
                new RabbitMqEventConsumer(connection2),
                services2.GetRequiredService<IServiceScopeFactory>(),
                Options.Create(new UnreadCounterConsumerOptions()),
                NullLogger<UnreadCounterConsumer>.Instance);

            await consumer2.StartAsync(CancellationToken.None);
            try
            {
                await ConcurrencyTestHelpers.WaitUntilAsync(
                    async () =>
                    {
                        await using var db = new AgoChatDbContext(dbOptions);
                        var conversation = await db.Conversations.FirstAsync(c => c.Id == conversationId, CancellationToken.None);
                        return conversation.OperatorUnreadCount >= MessageCount;
                    },
                    TimeSpan.FromSeconds(30));
            }
            finally
            {
                await consumer2.StopAsync(CancellationToken.None);
            }

            await using var verify = new AgoChatDbContext(dbOptions);
            var reloaded = await verify.Conversations.FirstAsync(c => c.Id == conversationId, CancellationToken.None);
            Assert.Equal(MessageCount, reloaded.OperatorUnreadCount);

            var inboxRowCount = await verify.Set<InboxRecord>()
                .CountAsync(r => messageIds.Contains(r.MessageId), CancellationToken.None);
            Assert.Equal(MessageCount, inboxRowCount);
        }
        finally
        {
            await postgres.DisposeAsync();
            await rabbitMq.DisposeAsync();
        }
    }

    private static ServiceProvider BuildServiceProvider(string connectionString)
    {
        var services = new ServiceCollection();
        services.AddPostgresPersistence(connectionString);
        services.AddPlatformKernel();
        services.AddScoped<Ago.Chat.Application.UseCases.RecordUnread.RecordUnreadMessageHandler>();
        return services.BuildServiceProvider();
    }
}
