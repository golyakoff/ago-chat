using Ago.Chat.Application.Abstractions;
using Ago.Chat.Application.UseCases.RecordUnread;
using Ago.Chat.Application.UseCases.SendMessage;
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

namespace Ago.Chat.Integration.Tests;

/// <summary>
/// 2-05's own "done when": a message sent through the real `2-02` handler path, dispatched by the
/// real `2-04` `OutboxDispatcher`, ends up incrementing the correct party's unread count via the
/// real `UnreadCounterConsumer` - observed by re-reading the row afterward, not by inspecting
/// either service's internal state.
///
/// Own, non-shared containers: this test publishes a real `MessageAccepted` onto the same topic
/// `OutboxDispatcherTests`' `Broadcast`-mode verification consumers subscribe to with an exact
/// count assertion - sharing `OutboxDispatcherFixture` was tried first and polluted that count
/// (found by running the full suite: `TwoDispatchers_RacingForTheSameBatch...` expected 20,
/// received 21 - this test's own message).
/// </summary>
public sealed class UnreadCounterEndToEndTests
{
    private const string Username = "ago-test";
    private const string Password = "ago-test-local-dev";
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task VisitorMessage_SentThroughTheRealHandlerChain_IncrementsTheOperatorsUnreadCount()
    {
        var postgres = new PostgreSqlBuilder("postgres:17-alpine").Build();
        var rabbitMq = new RabbitMqBuilder("rabbitmq:4-management").WithUsername(Username).WithPassword(Password).Build();
        await Task.WhenAll(postgres.StartAsync(), rabbitMq.StartAsync());

        try
        {
            await using var dataSource = new NpgsqlDataSourceBuilder(postgres.GetConnectionString()).Build();
            var dbOptions = new DbContextOptionsBuilder<AgoChatDbContext>().UseNpgsql(dataSource).Options;
            await using (var migrate = new AgoChatDbContext(dbOptions))
            {
                await migrate.Database.MigrateAsync();
            }

            var siteId = new SiteId(Guid.NewGuid());
            var visitorId = new VisitorId(Guid.NewGuid());
            var operatorId = new OperatorId(Guid.NewGuid());
            var conversationId = new ConversationId(Guid.NewGuid());

            await using (var seed = new AgoChatDbContext(dbOptions))
            {
                seed.Sites.Add(new Site(siteId, $"site_{siteId.Value:N}", []));
                seed.Visitors.Add(new Visitor(visitorId, siteId, Now));
                seed.Operators.Add(new Operator(operatorId, siteId, OperatorStatus.Online, capacity: 5));
                var conversation = Conversation.Start(conversationId, siteId, visitorId, Now);
                conversation.AssignTo(operatorId, Now);
                seed.Conversations.Add(conversation);
                await seed.SaveChangesAsync(CancellationToken.None);
            }

            var rabbitOptions = Options.Create(new RabbitMqOptions
            {
                HostName = rabbitMq.Hostname,
                Port = rabbitMq.GetMappedPublicPort(5672),
                UserName = Username,
                Password = Password,
            });

            await using var dispatcherConnection = new RabbitMqConnection(rabbitOptions);
            var dispatcher = new OutboxDispatcher(
                dataSource, new RabbitMqEventPublisher(dispatcherConnection), new SystemClock(),
                Options.Create(new OutboxDispatcherOptions { PollInterval = TimeSpan.FromSeconds(2), BatchSize = 20 }),
                NullLogger<OutboxDispatcher>.Instance);

            await using var services = BuildServiceProvider(dataSource);
            await using var consumerConnection = new RabbitMqConnection(rabbitOptions);
            var consumer = new UnreadCounterConsumer(
                new RabbitMqEventConsumer(consumerConnection),
                services.GetRequiredService<IServiceScopeFactory>(),
                Options.Create(new UnreadCounterConsumerOptions()),
                NullLogger<UnreadCounterConsumer>.Instance);

            await dispatcher.StartAsync(CancellationToken.None);
            await consumer.StartAsync(CancellationToken.None);
            try
            {
                // Both StartAsync calls return once their BackgroundService.ExecuteAsync task has
                // been kicked off, not once SubscribeAsync's queue declare+bind has actually landed
                // on the broker - without this, the dispatcher's own NOTIFY-driven publish (as soon
                // as the row below is inserted) can race ahead of the consumer's durable queue
                // existing, and a fanout exchange drops a message published before any queue is
                // bound to it rather than deferring it. 500ms measured flaky (failed roughly one run
                // in three) when the rest of the suite's own containers were also starting up
                // concurrently; 2s gave no further failures across repeated runs.
                await Task.Delay(TimeSpan.FromSeconds(2));

                // 2-02's real handler path - the same one an HTTP request would go through - stages
                // the message and the outbox row in one SaveChangesAsync.
                await using var db = new AgoChatDbContext(dbOptions);
                var handler = new SendVisitorMessageHandler(
                    new ConversationRepository(db), new FakeRateLimiter(), new MessageSendRateLimitOptions(),
                    new SynchronousMessagePipeline(dataSource));

                var result = await handler.HandleAsync(new SendVisitorMessage(conversationId, visitorId, "hello"), CancellationToken.None);
                Assert.True(result.IsSuccess);

                await OutboxTestHelpers.WaitUntilAsync(
                    async () =>
                    {
                        await using var verify = new AgoChatDbContext(dbOptions);
                        var conversation = await verify.Conversations.FirstAsync(c => c.Id == conversationId, CancellationToken.None);
                        return conversation.OperatorUnreadCount >= 1;
                    },
                    TimeSpan.FromSeconds(15));
            }
            finally
            {
                await dispatcher.StopAsync(CancellationToken.None);
                await consumer.StopAsync(CancellationToken.None);
            }

            await using var final = new AgoChatDbContext(dbOptions);
            var reloaded = await final.Conversations.FirstAsync(c => c.Id == conversationId, CancellationToken.None);
            Assert.Equal(1, reloaded.OperatorUnreadCount);
            Assert.Equal(0, reloaded.VisitorUnreadCount);
        }
        finally
        {
            await postgres.DisposeAsync();
            await rabbitMq.DisposeAsync();
        }
    }

    private static ServiceProvider BuildServiceProvider(NpgsqlDataSource dataSource)
    {
        var services = new ServiceCollection();
        services.AddSingleton(dataSource);
        services.AddDbContext<AgoChatDbContext>((provider, options) =>
            options.UseNpgsql(provider.GetRequiredService<NpgsqlDataSource>()));
        services.AddScoped<IConversationRepository, ConversationRepository>();
        services.AddOutboxInbox<AgoChatDbContext>();
        services.AddSingleton<IClock, SystemClock>();
        services.AddScoped<RecordUnreadMessageHandler>();
        return services.BuildServiceProvider();
    }
}
