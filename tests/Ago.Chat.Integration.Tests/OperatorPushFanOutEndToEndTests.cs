using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Ago.Chat.Application.Abstractions;
using Ago.Chat.Application.Mapping;
using Ago.Chat.Application.UseCases.NotifyOperatorDevices;
using Ago.Chat.Application.UseCases.RecordUnread;
using Ago.Chat.Application.UseCases.ResolveConversationAssignment;
using Ago.Chat.Contracts;
using Ago.Chat.Domain;
using Ago.Chat.Infrastructure.Postgres;
using Ago.Chat.Infrastructure.Postgres.Persistence;
using Ago.Chat.Worker;
using Ago.Platform.Abstractions;
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
/// `26-05`/`push-notifications.md`'s own "Fan-out" section, proven against real Postgres and real
/// RabbitMQ - own, non-shared containers per test method, the identical `UnreadCounterEndToEndTests`
/// shape, since each test owns a `Competing` subscription's exact queue name and sharing a broker
/// across test classes has already caused one cross-test count pollution in this suite
/// (`UnreadCounterEndToEndTests`' own remarks).
///
/// <para><b>What each test proves, and why it is a real test rather than an inference.</b> The first
/// proves both this item's own Done-when items about `ConversationAssignedToOperator`: a real push
/// reaches the assigned operator's device, <em>and</em> a real conversation transfer - run through the
/// actual production `ConversationTransferredMapper`, not a hand-built substitute - reaches the new
/// assignee, all while `OperatorAssignmentPushConsumer` coexists with the pre-existing
/// `ConversationAssignmentFanoutConsumer` on the same topic without either splitting the other's count
/// (the `5-11`/`WebhookDispatchSharedQueueRegressionTests` shape, reapplied to this new subscriber).
/// The second proves the message-side Done-when: a visitor-authored `MessageAccepted` fires a push
/// while an operator-authored one does not, and `OperatorMessagePushConsumer` - a new competing
/// subscriber added to an already-crowded `MessageAccepted` topic - does not disrupt an existing
/// sibling (`UnreadCounterConsumer`), confirmed live rather than assumed from unique `ConsumerName`s
/// alone.</para>
/// </summary>
public sealed class OperatorPushFanOutEndToEndTests
{
    private const string Username = "ago-test";
    private const string Password = "ago-test-local-dev";
    private const int RabbitMqManagementPort = 15672;

    private static readonly DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task AssignmentEvent_FiresAPushToTheAssignedOperatorsDevice_AlongsideTheExistingFanoutConsumer_AndATransferReachesTheNewAssignee()
    {
        var postgres = new PostgreSqlBuilder("postgres:17-alpine").Build();
        var rabbitMq = new RabbitMqBuilder("rabbitmq:4-management")
            .WithUsername(Username).WithPassword(Password)
            .WithPortBinding(RabbitMqManagementPort, true)
            .Build();
        await Task.WhenAll(postgres.StartAsync(), rabbitMq.StartAsync());

        const int MessageCount = 4;

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
            var operatorA = new OperatorId(Guid.NewGuid());
            var operatorB = new OperatorId(Guid.NewGuid());
            var conversationId = new ConversationId(Guid.NewGuid());

            await using (var seed = new AgoChatDbContext(dbOptions))
            {
                seed.Sites.Add(new Site(siteId, $"site_{siteId.Value:N}", []));
                seed.Visitors.Add(new Visitor(visitorId, siteId, Now));
                seed.Operators.Add(new Operator(operatorA, siteId, OperatorStatus.Online, capacity: 5));
                seed.Operators.Add(new Operator(operatorB, siteId, OperatorStatus.Online, capacity: 5));
                var seededConversation = Conversation.Start(conversationId, siteId, visitorId, Now);
                seededConversation.AssignTo(operatorA, Now);
                seed.Conversations.Add(seededConversation);
                seed.OperatorDevices.Add(OperatorDevice.Register(
                    new OperatorDeviceId(Guid.NewGuid()), siteId, operatorA, "install-a", PushProvider.RuStore, "android", "token-a", Now));
                seed.OperatorDevices.Add(OperatorDevice.Register(
                    new OperatorDeviceId(Guid.NewGuid()), siteId, operatorB, "install-b", PushProvider.RuStore, "android", "token-b", Now));
                await seed.SaveChangesAsync(CancellationToken.None);
            }

            var rabbitOptions = Options.Create(new RabbitMqOptions
            {
                HostName = rabbitMq.Hostname,
                Port = rabbitMq.GetMappedPublicPort(5672),
                UserName = Username,
                Password = Password,
            });

            var pushSender = new RecordingPushSender();
            var fanoutPublisher = new RecordingNodeFanoutPublisher();
            await using var services = BuildAssignmentServiceProvider(dataSource, pushSender, fanoutPublisher);

            await using var pushConsumerConnection = new RabbitMqConnection(rabbitOptions, NullLogger<RabbitMqConnection>.Instance);
            var pushConsumer = new OperatorAssignmentPushConsumer(
                new RabbitMqEventConsumer(pushConsumerConnection), services.GetRequiredService<IServiceScopeFactory>(),
                Options.Create(new OperatorAssignmentPushConsumerOptions()), NullLogger<OperatorAssignmentPushConsumer>.Instance);

            await using var fanoutConsumerConnection = new RabbitMqConnection(rabbitOptions, NullLogger<RabbitMqConnection>.Instance);
            var fanoutConsumer = new ConversationAssignmentFanoutConsumer(
                new RabbitMqEventConsumer(fanoutConsumerConnection), services.GetRequiredService<IServiceScopeFactory>(),
                Options.Create(new ConversationAssignmentFanoutConsumerOptions()), NullLogger<ConversationAssignmentFanoutConsumer>.Instance);

            using var management = CreateRabbitMqManagementClient(rabbitMq, Username, Password);

            await pushConsumer.StartAsync(CancellationToken.None);
            await fanoutConsumer.StartAsync(CancellationToken.None);
            try
            {
                // Both own their own queue - the `26-05` regression this whole file exists to catch
                // would show up here as one of these two never reaching a live consumer at all.
                await RabbitMqSubscriptionTestHelpers.AwaitAllCompetingSubscriptionsAsync(
                    management, TimeSpan.FromSeconds(10),
                    (nameof(ConversationAssignedToOperator), OperatorAssignmentPushConsumer.ConsumerName),
                    (nameof(ConversationAssignedToOperator), ConversationAssignmentFanoutConsumer.ConsumerName));

                await using var publisherConnection = new RabbitMqConnection(rabbitOptions, NullLogger<RabbitMqConnection>.Instance);
                var publisher = new RabbitMqEventPublisher(publisherConnection, NullLogger<RabbitMqEventPublisher>.Instance);

                for (var i = 0; i < MessageCount; i++)
                {
                    await publisher.PublishAsync(BuildAssignmentEnvelope(siteId, visitorId, operatorA, conversationId), CancellationToken.None);
                }

                var pushCaughtUp = await OutboxTestHelpers.WaitUntilAsync(() => pushSender.Calls.Count >= MessageCount, TimeSpan.FromSeconds(15));
                var fanoutCaughtUp = await OutboxTestHelpers.WaitUntilAsync(() => fanoutPublisher.Calls.Count >= MessageCount, TimeSpan.FromSeconds(15));
                Assert.True(pushCaughtUp, $"Push consumer received {pushSender.Calls.Count}/{MessageCount} - the two consumers may be splitting one queue.");
                Assert.True(fanoutCaughtUp, $"Fanout consumer received {fanoutPublisher.Calls.Count}/{MessageCount} - the two consumers may be splitting one queue.");
                // Each independently equals MessageCount - not summing to it, which is what the
                // pre-5-11 shared-queue bug would have produced.
                Assert.Equal(MessageCount, pushSender.Calls.Count);
                Assert.Equal(MessageCount, fanoutPublisher.Calls.Count);
                Assert.All(pushSender.Calls, call => Assert.Equal("token-a", call.DeviceToken));

                // Done-when #2: a transfer, run through the real ConversationTransferredMapper, reaches
                // the new assignee - not inferred from the mapper existing.
                var domainEvent = new ConversationTransferred(conversationId, operatorA, operatorB, Now);
                var envelope = ConversationTransferredMapper.ToEnvelope(domainEvent, siteId, visitorId, new UuidV7Generator());
                await publisher.PublishAsync(envelope, CancellationToken.None);

                var transferCaughtUp = await OutboxTestHelpers.WaitUntilAsync(
                    () => pushSender.Calls.Any(call => call.DeviceToken == "token-b"), TimeSpan.FromSeconds(15));
                Assert.True(transferCaughtUp, "Timed out waiting for the transfer's own push to reach the new assignee's device.");
                // The transfer's own push went to the new assignee only - operatorA's own count is
                // still exactly the four assignment pushes above, not five.
                Assert.Equal(MessageCount, pushSender.Calls.Count(call => call.DeviceToken == "token-a"));
                Assert.Equal(1, pushSender.Calls.Count(call => call.DeviceToken == "token-b"));
            }
            finally
            {
                await pushConsumer.StopAsync(CancellationToken.None);
                await fanoutConsumer.StopAsync(CancellationToken.None);
            }
        }
        finally
        {
            await postgres.DisposeAsync();
            await rabbitMq.DisposeAsync();
        }
    }

    [Fact]
    public async Task MessageAcceptedEvent_VisitorAuthored_FiresPushAndDoesNotDisruptTheExistingUnreadCounterConsumer_WhileOperatorAuthoredIsFiltered()
    {
        var postgres = new PostgreSqlBuilder("postgres:17-alpine").Build();
        var rabbitMq = new RabbitMqBuilder("rabbitmq:4-management")
            .WithUsername(Username).WithPassword(Password)
            .WithPortBinding(RabbitMqManagementPort, true)
            .Build();
        await Task.WhenAll(postgres.StartAsync(), rabbitMq.StartAsync());

        const int VisitorMessageCount = 5;

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
                var seededConversation = Conversation.Start(conversationId, siteId, visitorId, Now);
                seededConversation.AssignTo(operatorId, Now);
                seed.Conversations.Add(seededConversation);
                seed.OperatorDevices.Add(OperatorDevice.Register(
                    new OperatorDeviceId(Guid.NewGuid()), siteId, operatorId, "install-1", PushProvider.RuStore, "android", "token-1", Now));
                await seed.SaveChangesAsync(CancellationToken.None);
            }

            var rabbitOptions = Options.Create(new RabbitMqOptions
            {
                HostName = rabbitMq.Hostname,
                Port = rabbitMq.GetMappedPublicPort(5672),
                UserName = Username,
                Password = Password,
            });

            var pushSender = new RecordingPushSender();
            await using var services = BuildMessageServiceProvider(dataSource, pushSender);

            await using var pushConsumerConnection = new RabbitMqConnection(rabbitOptions, NullLogger<RabbitMqConnection>.Instance);
            var pushConsumer = new OperatorMessagePushConsumer(
                new RabbitMqEventConsumer(pushConsumerConnection), services.GetRequiredService<IServiceScopeFactory>(),
                Options.Create(new OperatorMessagePushConsumerOptions()), NullLogger<OperatorMessagePushConsumer>.Instance);

            await using var unreadConsumerConnection = new RabbitMqConnection(rabbitOptions, NullLogger<RabbitMqConnection>.Instance);
            var unreadConsumer = new UnreadCounterConsumer(
                new RabbitMqEventConsumer(unreadConsumerConnection), services.GetRequiredService<IServiceScopeFactory>(),
                Options.Create(new UnreadCounterConsumerOptions()), NullLogger<UnreadCounterConsumer>.Instance);

            using var management = CreateRabbitMqManagementClient(rabbitMq, Username, Password);

            await pushConsumer.StartAsync(CancellationToken.None);
            await unreadConsumer.StartAsync(CancellationToken.None);
            try
            {
                // `push-notifications.md` itself calls this "a fifth" subscriber (the four it counted
                // when written); the topic has grown since, but the property under test is unchanged:
                // adding one more Competing subscriber, each with its own ConsumerName, must not touch
                // an existing sibling's own queue.
                await RabbitMqSubscriptionTestHelpers.AwaitAllCompetingSubscriptionsAsync(
                    management, TimeSpan.FromSeconds(10),
                    (nameof(MessageAccepted), OperatorMessagePushConsumer.ConsumerName),
                    (nameof(MessageAccepted), RecordUnreadMessageHandler.ConsumerName));

                await using var publisherConnection = new RabbitMqConnection(rabbitOptions, NullLogger<RabbitMqConnection>.Instance);
                var publisher = new RabbitMqEventPublisher(publisherConnection, NullLogger<RabbitMqEventPublisher>.Instance);

                for (var sequence = 1; sequence <= VisitorMessageCount; sequence++)
                {
                    await publisher.PublishAsync(
                        BuildMessageEnvelope(siteId, conversationId, Guid.NewGuid(), nameof(MessageAuthorKind.Visitor), sequence),
                        CancellationToken.None);
                }

                // `alerts.ts`'s own echo rule: an operator-authored message must reach the existing
                // sibling (so its own unread count still moves) but must never reach a push.
                await publisher.PublishAsync(
                    BuildMessageEnvelope(siteId, conversationId, Guid.NewGuid(), nameof(MessageAuthorKind.Operator), VisitorMessageCount + 1),
                    CancellationToken.None);

                var unreadCaughtUp = await OutboxTestHelpers.WaitUntilAsync(
                    async () =>
                    {
                        await using var verify = new AgoChatDbContext(dbOptions);
                        var reloaded = await verify.Conversations.FirstAsync(c => c.Id == conversationId, CancellationToken.None);
                        return reloaded.OperatorUnreadCount >= VisitorMessageCount && reloaded.VisitorUnreadCount >= 1;
                    },
                    TimeSpan.FromSeconds(15));
                Assert.True(unreadCaughtUp, "UnreadCounterConsumer did not process all six messages - the new fifth subscriber may have disrupted it.");

                var pushCaughtUp = await OutboxTestHelpers.WaitUntilAsync(() => pushSender.Calls.Count >= VisitorMessageCount, TimeSpan.FromSeconds(15));
                Assert.True(pushCaughtUp, $"Push consumer received {pushSender.Calls.Count}/{VisitorMessageCount} visitor-authored messages.");
            }
            finally
            {
                await pushConsumer.StopAsync(CancellationToken.None);
                await unreadConsumer.StopAsync(CancellationToken.None);
            }

            await using var final = new AgoChatDbContext(dbOptions);
            var conversation = await final.Conversations.FirstAsync(c => c.Id == conversationId, CancellationToken.None);
            // Both siblings independently saw every one of the six messages - not a split between
            // them - and the push consumer's own visitor-only filter held under real delivery too.
            Assert.Equal(VisitorMessageCount, conversation.OperatorUnreadCount);
            Assert.Equal(1, conversation.VisitorUnreadCount);
            Assert.Equal(VisitorMessageCount, pushSender.Calls.Count);
        }
        finally
        {
            await postgres.DisposeAsync();
            await rabbitMq.DisposeAsync();
        }
    }

    private static ServiceProvider BuildAssignmentServiceProvider(
        NpgsqlDataSource dataSource, IPushSender pushSender, INodeFanoutPublisher fanoutPublisher)
    {
        var services = new ServiceCollection();
        services.AddSingleton(dataSource);
        services.AddDbContext<AgoChatDbContext>((provider, options) =>
            options.UseNpgsql(provider.GetRequiredService<NpgsqlDataSource>()));
        services.AddScoped<IConversationRepository, ConversationRepository>();
        services.AddScoped<IOperatorDeviceRepository, OperatorDeviceRepository>();
        services.AddSingleton<IClock, SystemClock>();
        services.AddSingleton(pushSender);
        services.AddSingleton(fanoutPublisher);
        services.AddScoped<NotifyOperatorDevicesHandler>();
        services.AddScoped<ResolveConversationAssignmentTargetsHandler>();
        return services.BuildServiceProvider();
    }

    private static ServiceProvider BuildMessageServiceProvider(NpgsqlDataSource dataSource, IPushSender pushSender)
    {
        var services = new ServiceCollection();
        services.AddSingleton(dataSource);
        services.AddDbContext<AgoChatDbContext>((provider, options) =>
            options.UseNpgsql(provider.GetRequiredService<NpgsqlDataSource>()));
        services.AddScoped<IConversationRepository, ConversationRepository>();
        services.AddScoped<IOperatorDeviceRepository, OperatorDeviceRepository>();
        services.AddScoped<IUnreadCounterStore, UnreadCounterStore>();
        services.AddScoped<IUnitOfWork, EfUnitOfWork>();
        services.AddOutboxInbox<AgoChatDbContext>();
        services.AddSingleton<IClock, SystemClock>();
        services.AddSingleton(pushSender);
        services.AddScoped<NotifyOperatorDevicesHandler>();
        services.AddScoped<RecordUnreadMessageHandler>();
        return services.BuildServiceProvider();
    }

    private static EventEnvelope BuildAssignmentEnvelope(SiteId siteId, VisitorId visitorId, OperatorId operatorId, ConversationId conversationId)
    {
        var contract = new ConversationAssignedToOperator(
            ConversationId: conversationId.Value, SiteId: siteId.Value, VisitorId: visitorId.Value, OperatorId: operatorId.Value,
            CorrelationId: Guid.NewGuid(), OccurredAt: Now);

        return new EventEnvelope(
            MessageId: Guid.NewGuid(),
            Type: nameof(ConversationAssignedToOperator),
            Version: 1,
            PartitionKey: contract.ConversationId.ToString(),
            OccurredAt: Now,
            CorrelationId: contract.CorrelationId,
            Payload: JsonSerializer.Serialize(contract));
    }

    private static EventEnvelope BuildMessageEnvelope(
        SiteId siteId, ConversationId conversationId, Guid messageId, string authorKind, int sequence)
    {
        var contract = new MessageAccepted(
            MessageId: messageId, OccurredAt: Now, SiteId: siteId.Value, CorrelationId: Guid.NewGuid(),
            ConversationId: conversationId.Value, AuthorKind: authorKind, Sequence: sequence);

        return new EventEnvelope(
            MessageId: Guid.NewGuid(),
            Type: nameof(MessageAccepted),
            Version: 1,
            PartitionKey: contract.ConversationId.ToString(),
            OccurredAt: Now,
            CorrelationId: contract.CorrelationId,
            Payload: JsonSerializer.Serialize(contract));
    }

    private static HttpClient CreateRabbitMqManagementClient(RabbitMqContainer rabbitMq, string username, string password)
    {
        var client = new HttpClient
        {
            BaseAddress = new Uri($"http://{rabbitMq.Hostname}:{rabbitMq.GetMappedPublicPort(RabbitMqManagementPort)}"),
        };
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{username}:{password}")));
        return client;
    }

    /// <summary>The `IPushSender` counterpart to `WebhookDispatchSharedQueueRegressionTests`' own
    /// `RecordingNodeFanoutPublisher` - every call always <see cref="PushSendOutcome.Delivered"/>,
    /// since this file's own tests are about which consumer receives which event, not about
    /// `26-04`'s own outcome classification (already proven by `RuStorePushSenderTests`).</summary>
    private sealed class RecordingPushSender : IPushSender
    {
        public ConcurrentBag<PushMessage> Calls { get; } = [];

        public Task<PushSendOutcome> SendAsync(PushMessage message, CancellationToken cancellationToken)
        {
            Calls.Add(message);
            return Task.FromResult<PushSendOutcome>(new PushSendOutcome.Delivered());
        }
    }

    private sealed class RecordingNodeFanoutPublisher : INodeFanoutPublisher
    {
        public ConcurrentBag<string> Calls { get; } = [];

        public Task<FanoutResult> PublishAsync(
            IReadOnlyCollection<PrincipalKey> recipients, string method, string payloadJson, Guid correlationId,
            CancellationToken cancellationToken)
        {
            Calls.Add(payloadJson);
            return Task.FromResult(new FanoutResult([.. recipients.Select(recipient => new ResolvedRecipient(recipient, 0))]));
        }
    }
}
