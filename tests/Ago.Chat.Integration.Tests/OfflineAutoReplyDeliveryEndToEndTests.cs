using System.Collections.Concurrent;
using System.Text.Json;
using Ago.Chat.Application.Abstractions;
using Ago.Chat.Application.Realtime;
using Ago.Chat.Application.UseCases.GetSiteConfigById;
using Ago.Chat.Application.UseCases.ResolveMessageDelivery;
using Ago.Chat.Application.UseCases.SendMessage;
using Ago.Chat.Application.UseCases.SendOfflineAutoReply;
using Ago.Chat.Application.UseCases.UpdateOfflineAutoReply;
using Ago.Chat.Contracts;
using Ago.Chat.Domain;
using Ago.Chat.Infrastructure.Postgres;
using Ago.Chat.Infrastructure.Postgres.Persistence;
using Ago.Chat.Worker;
using Ago.Platform.Abstractions;
using Ago.Platform.Caching.Redis;
using Ago.Platform.Hosting;
using Ago.Platform.Kernel;
using Ago.Platform.Messaging.RabbitMq;
using Ago.Platform.Persistence.Postgres;
using Ago.Platform.Realtime;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Polly;

namespace Ago.Chat.Integration.Tests;

/// <summary>
/// `14-04`'s second Done-when - "an offline visitor gets a real automatic reply, not just a passing
/// unit test" - at the highest level this repository can reach without a browser. Every stage is the
/// real one, wired by hand the way <see cref="ConnectionFanoutEndToEndTests"/> already wires its own:
///
/// <code>
/// SendVisitorMessageHandler -> messages row + outbox (real Postgres)
///   -> OutboxDispatcher -> RabbitMQ -> MessageAccepted
///     -> OfflineAutoReplyConsumer -> SendOfflineAutoReplyHandler -> the reply row + its own outbox row
///       -> OutboxDispatcher -> RabbitMQ -> MessageAccepted (the reply's own)
///         -> ConnectionFanoutConsumer -> NodeFanoutPublisher -> NodeDeliveryConsumer
///           -> the visitor's own connection, as "MessageReceived"
/// </code>
///
/// <para>The only fake is <see cref="ILocalConnectionDispatcher"/> - the SignalR-facing edge
/// <c>Ago.Chat.Api</c> owns, and the one port genuinely outside this process. What arrives at it is
/// byte-for-byte the frame the widget's <c>MessageReceived</c> handler parses, so the remaining
/// unverified step is the browser rendering it, which <c>ago-widget</c>'s own test covers.</para>
///
/// <para><b>It also settles the loop question against the real broker rather than in a fixture.</b>
/// The reply's <c>MessageAccepted</c> genuinely goes back round to the same
/// <see cref="OfflineAutoReplyConsumer"/> that produced it - there is no filtering on the topic and
/// nothing stops the delivery. The assertion is that exactly one auto-reply exists after all of it
/// has settled.</para>
/// </summary>
[Collection(ConnectionFanoutCollection.Name)]
public sealed class OfflineAutoReplyDeliveryEndToEndTests(ConnectionFanoutFixture fixture)
{
    private static readonly DateTimeOffset Now = new(DateTimeOffset.UtcNow.Ticks / TimeSpan.TicksPerSecond * TimeSpan.TicksPerSecond, TimeSpan.Zero);
    private const string Fallback = "We are closed - we will reply in the morning.";

    [Fact]
    public async Task AnOfflineVisitorsMessage_IsAnsweredOnceAndDeliveredToTheirOwnConnection()
    {
        var siteId = new SiteId(Guid.NewGuid());
        var visitorId = new VisitorId(Guid.NewGuid());
        var operatorId = new OperatorId(Guid.NewGuid());
        var conversationId = new ConversationId(Guid.NewGuid());
        var roleId = Guid.NewGuid();

        await using (var seed = fixture.CreateDbContext())
        {
            seed.Sites.Add(new Site(siteId, $"site_{siteId.Value:N}", []));
            seed.Visitors.Add(new Visitor(visitorId, siteId, Now));
            // Offline: the shop has a team, and none of them is on duty. Exactly the condition this
            // feature exists for, and not the same thing as having no operators at all.
            seed.Operators.Add(new Operator(operatorId, siteId, OperatorStatus.Offline, capacity: 5));
            seed.Roles.Add(new RoleRecord
            {
                Id = roleId,
                SiteId = siteId,
                Name = "Admin",
                Permissions = [Permission.SiteConfigure.Value],
            });
            seed.OperatorRoles.Add(new OperatorRoleRecord { OperatorId = operatorId, RoleId = roleId });
            // Waiting - nobody has picked it up, which is where an unattended conversation sits.
            seed.Conversations.Add(Conversation.Start(conversationId, siteId, visitorId, Now));
            await seed.SaveChangesAsync(CancellationToken.None);
        }

        // The tenant switches it on from the console's own use case, not by writing a column.
        await using (var configDb = fixture.CreateDbContext())
        {
            var update = new UpdateOfflineAutoReplyHandler(
                new SiteRepository(configDb), new PermissionChecker(configDb),
                new EfOutboxWriter<AgoChatDbContext>(configDb), new UuidV7Generator(), new SystemClock());
            var configured = await update.HandleAsync(
                new UpdateOfflineAutoReply(
                    siteId, operatorId, Enabled: true, Fallback,
                    [new UpdateOfflineAutoReplyRule("refund", "Refunds take three working days.")]),
                CancellationToken.None);
            Assert.True(configured.IsSuccess, configured.IsFailure ? configured.Error!.Value.Message : null);
        }

        var registry = new RedisConnectionRegistry(
            fixture.RedisMultiplexer, Options.Create(new ConnectionRegistryOptions()), NullLogger<RedisConnectionRegistry>.Instance);
        var node = new NodeId($"node-{Guid.NewGuid():N}");
        var visitorConnection = new ConnectionId(Guid.NewGuid().ToString());
        await registry.RegisterAsync(visitorConnection, node, PrincipalKeys.ForVisitor(visitorId), CancellationToken.None);

        await using var fanoutPublisherConnection = fixture.CreateRabbitMqConnection();
        await using var services = BuildServiceProvider(fanoutPublisherConnection);
        var scopeFactory = services.GetRequiredService<IServiceScopeFactory>();

        await using var dispatcherConnection = fixture.CreateRabbitMqConnection();
        var dispatcher = new OutboxDispatcher(
            fixture.DataSource, new RabbitMqEventPublisher(dispatcherConnection, NullLogger<RabbitMqEventPublisher>.Instance), new SystemClock(),
            Options.Create(new OutboxDispatcherOptions { PollInterval = TimeSpan.FromMilliseconds(500) }),
            NullLogger<OutboxDispatcher>.Instance);

        await using var autoReplyConsumerConnection = fixture.CreateRabbitMqConnection();
        var autoReplyConsumer = new OfflineAutoReplyConsumer(
            new RabbitMqEventConsumer(autoReplyConsumerConnection), scopeFactory,
            Options.Create(new OfflineAutoReplyConsumerOptions()), NullLogger<OfflineAutoReplyConsumer>.Instance);

        await using var fanoutConsumerConnection = fixture.CreateRabbitMqConnection();
        var fanoutConsumer = new ConnectionFanoutConsumer(
            new RabbitMqEventConsumer(fanoutConsumerConnection), scopeFactory,
            Options.Create(new ConnectionFanoutConsumerOptions()), NullLogger<ConnectionFanoutConsumer>.Instance);

        var localDispatcher = new FakeLocalConnectionDispatcher();
        await using var nodeConsumerConnection = fixture.CreateRabbitMqConnection();
        var nodeConsumer = new NodeDeliveryConsumer(
            new RabbitMqEventConsumer(nodeConsumerConnection), localDispatcher, node, NullLogger<NodeDeliveryConsumer>.Instance);

        await dispatcher.StartAsync(CancellationToken.None);
        await autoReplyConsumer.StartAsync(CancellationToken.None);
        await fanoutConsumer.StartAsync(CancellationToken.None);
        await nodeConsumer.StartAsync(CancellationToken.None);

        // `15-17`: wait for the fact each Competing subscription's own queue has a live consumer
        // attached, not merely that the queue exists - see WebhookDispatchSharedQueueRegressionTests'
        // own remarks for why StartAsync alone cannot be awaited for this, and
        // RabbitMqSubscriptionTestHelpers' own remarks for why "the queue exists" is not enough.
        // autoReplyConsumer and fanoutConsumer are two independent Competing subscribers of the *same*
        // MessageAccepted topic (5-11's own shape) - each needs its own queue, so both are waited for
        // explicitly rather than assuming one implies the other.
        using var subscriptionManagementClient = fixture.CreateRabbitMqManagementClient();
        await RabbitMqSubscriptionTestHelpers.AwaitAllCompetingSubscriptionsAsync(
            subscriptionManagementClient, TimeSpan.FromSeconds(10),
            (nameof(MessageAccepted), SendOfflineAutoReplyHandler.ConsumerName),
            (nameof(MessageAccepted), ConnectionFanoutConsumer.ConsumerName),
            (NodeTopics.For(node), RabbitMqSubscriptionTestHelpers.NodeDeliveryConsumerName));

        try
        {
            await using (var writeDb = fixture.CreateDbContext())
            {
                var send = new SendVisitorMessageHandler(
                    new ConversationRepository(writeDb), new FakeRateLimiter(), new MessageSendRateLimitOptions(),
                    new SynchronousMessagePipeline(fixture.DataSource));

                var sent = await send.HandleAsync(
                    new SendVisitorMessage(conversationId, visitorId, "hello? is anybody there?"), CancellationToken.None);
                Assert.True(sent.IsSuccess, sent.IsFailure ? sent.Error!.Value.Message : null);
            }

            var delivered = await OutboxTestHelpers.WaitUntilAsync(
                () => localDispatcher.Dispatches.Any(d => d.PayloadJson.Contains(Fallback, StringComparison.Ordinal)),
                TimeSpan.FromSeconds(30));
            Assert.True(delivered, "Timed out waiting for the scripted reply to reach the visitor's own connection.");

            // The reply's own MessageAccepted has now been on the topic. Give the loop the time it
            // would need to close, if it could - a reply to the reply would land well inside this.
            await Task.Delay(TimeSpan.FromSeconds(3));
        }
        finally
        {
            await dispatcher.StopAsync(CancellationToken.None);
            await autoReplyConsumer.StopAsync(CancellationToken.None);
            await fanoutConsumer.StopAsync(CancellationToken.None);
            await nodeConsumer.StopAsync(CancellationToken.None);
        }

        await using var verify = fixture.CreateDbContext();
        var conversation = await verify.Conversations
            .Include("_messages")
            .SingleAsync(c => c.Id == conversationId, CancellationToken.None);

        // Exactly one auto-reply, ever. The loop guard, against a real broker that really did deliver
        // the reply's own event back to the consumer that wrote it.
        var reply = Assert.Single(conversation.Messages, m => m.AuthorKind == MessageAuthorKind.System);
        Assert.Equal(Fallback, reply.Body.Value);
        Assert.Equal(2, conversation.Messages.Count);

        // What the widget actually receives: the same MessageDto shape every other message arrives
        // as, carrying "System" so the panel can label it rather than pass it off as a person.
        var frame = Assert.Single(
            localDispatcher.Dispatches, d => d.PayloadJson.Contains(Fallback, StringComparison.Ordinal));
        Assert.Equal(visitorConnection, frame.ConnectionId);
        Assert.Equal("MessageReceived", frame.Method);
        var dto = JsonSerializer.Deserialize<MessageDto>(frame.PayloadJson, WireJsonOptions.Options);
        Assert.Equal(nameof(MessageAuthorKind.System), dto!.AuthorKind);
        Assert.Equal(Fallback, dto.Body);
    }

    private ServiceProvider BuildServiceProvider(RabbitMqConnection fanoutPublisherConnection)
    {
        var services = new ServiceCollection();
        services.AddDbContext<AgoChatDbContext>(options => options.UseNpgsql(fixture.DataSource));
        services.AddScoped<IConversationRepository, ConversationRepository>();
        services.AddScoped<ISiteRepository, SiteRepository>();
        services.AddScoped<IOperatorRepository, OperatorRepository>();
        services.AddScoped<IConversationReadStore>(_ => new ConversationReadStore(fixture.DataSource));
        services.AddSingleton<ICache>(_ => new RedisCache(
            fixture.RedisMultiplexer,
            new ResiliencePipelineBuilder().AddTimeout(TimeSpan.FromSeconds(2)).Build(),
            NullLogger<RedisCache>.Instance));
        services.AddSingleton<IConnectionRegistry>(_ => new RedisConnectionRegistry(
            fixture.RedisMultiplexer, Options.Create(new ConnectionRegistryOptions()), NullLogger<RedisConnectionRegistry>.Instance));
        // See ConnectionFanoutEndToEndTests' own note on why this is not registered as disposable.
        services.AddSingleton<IEventPublisher>(_ => new RabbitMqEventPublisher(fanoutPublisherConnection, NullLogger<RabbitMqEventPublisher>.Instance));
        services.AddSingleton<IClock, SystemClock>();
        services.AddSingleton<IIdGenerator, UuidV7Generator>();
        services.AddSingleton<INodeFanoutPublisher, NodeFanoutPublisher>();
        services.AddScoped<IOutboxWriter, EfOutboxWriter<AgoChatDbContext>>();
        services.AddScoped<IInboxChecker, EfInboxChecker<AgoChatDbContext>>();
        services.AddScoped<GetSiteConfigByIdHandler>();
        services.AddScoped<SendOfflineAutoReplyHandler>();
        services.AddScoped<ResolveMessageDeliveryTargetsHandler>();
        return services.BuildServiceProvider();
    }

    private sealed class FakeLocalConnectionDispatcher : ILocalConnectionDispatcher
    {
        public ConcurrentBag<(ConnectionId ConnectionId, string Method, string PayloadJson)> Dispatches { get; } = [];

        public Task<DispatchOutcome> DispatchAsync(ConnectionId connectionId, string method, string payloadJson, CancellationToken cancellationToken)
        {
            Dispatches.Add((connectionId, method, payloadJson));
            return Task.FromResult(DispatchOutcome.Delivered);
        }
    }
}
