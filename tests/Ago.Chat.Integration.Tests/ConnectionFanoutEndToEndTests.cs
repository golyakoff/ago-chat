using System.Collections.Concurrent;
using Ago.Chat.Application.Abstractions;
using Ago.Chat.Application.Realtime;
using Ago.Chat.Application.UseCases.ResolveMessageDelivery;
using Ago.Chat.Application.UseCases.SendMessage;
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
using Ago.Platform.Realtime;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Ago.Chat.Integration.Tests;

/// <summary>
/// 3-02's backlog item, at the product level: the concrete proof behind Stage 3's "three Api
/// replicas serve one conversation correctly." A visitor connection registered under "node A" and an
/// operator connection under "node B" - sending a message through the real handler chain
/// (`SendVisitorMessageHandler` -&gt; outbox -&gt; `OutboxDispatcher` -&gt; `MessageAccepted` -&gt;
/// `ConnectionFanoutConsumer` -&gt; `ResolveMessageDeliveryTargetsHandler` -&gt; `NodeFanoutPublisher`) must
/// reach node B's own delivery consumer for the operator's connection, not node A's. Real Postgres,
/// real RabbitMQ, real Redis; a hand-written fake only for the one port genuinely outside this
/// process's own code - `ILocalConnectionDispatcher`, the SignalR-facing edge `Ago.Chat.Api` owns.
/// </summary>
[Collection(ConnectionFanoutCollection.Name)]
public sealed class ConnectionFanoutEndToEndTests(ConnectionFanoutFixture fixture)
{
    private static readonly DateTimeOffset Now = new(DateTimeOffset.UtcNow.Ticks / TimeSpan.TicksPerSecond * TimeSpan.TicksPerSecond, TimeSpan.Zero);

    [Fact]
    public async Task VisitorMessage_SentThroughTheRealHandlerChain_ReachesTheOperatorsConnectionOnItsOwnNode()
    {
        var siteId = new SiteId(Guid.NewGuid());
        var visitorId = new VisitorId(Guid.NewGuid());
        var operatorId = new OperatorId(Guid.NewGuid());
        var conversationId = new ConversationId(Guid.NewGuid());

        await using (var seed = fixture.CreateDbContext())
        {
            seed.Sites.Add(new Site(siteId, $"site_{siteId.Value:N}", []));
            seed.Visitors.Add(new Visitor(visitorId, siteId, Now));
            seed.Operators.Add(new Operator(operatorId, siteId, OperatorStatus.Online, capacity: 5));
            var conversation = Conversation.Start(conversationId, siteId, visitorId, Now);
            conversation.AssignTo(operatorId, Now);
            seed.Conversations.Add(conversation);
            await seed.SaveChangesAsync(CancellationToken.None);
        }

        var registry = new RedisConnectionRegistry(
            fixture.RedisMultiplexer, Options.Create(new ConnectionRegistryOptions()), NullLogger<RedisConnectionRegistry>.Instance);
        var nodeA = new NodeId($"node-a-{Guid.NewGuid():N}");
        var nodeB = new NodeId($"node-b-{Guid.NewGuid():N}");
        var visitorConnection = new ConnectionId(Guid.NewGuid().ToString());
        var operatorConnection = new ConnectionId(Guid.NewGuid().ToString());
        await registry.RegisterAsync(visitorConnection, nodeA, PrincipalKeys.ForVisitor(visitorId), CancellationToken.None);
        await registry.RegisterAsync(operatorConnection, nodeB, PrincipalKeys.ForOperator(operatorId), CancellationToken.None);

        await using var fanoutPublisherConnection = fixture.CreateRabbitMqConnection();
        await using var services = BuildServiceProvider(fanoutPublisherConnection);

        await using var dispatcherConnection = fixture.CreateRabbitMqConnection();
        var dispatcher = new OutboxDispatcher(
            fixture.DataSource, new RabbitMqEventPublisher(dispatcherConnection, NullLogger<RabbitMqEventPublisher>.Instance), new SystemClock(),
            Options.Create(new OutboxDispatcherOptions { PollInterval = TimeSpan.FromSeconds(2) }), NullLogger<OutboxDispatcher>.Instance);

        await using var fanoutConsumerConnection = fixture.CreateRabbitMqConnection();
        var fanoutConsumer = new ConnectionFanoutConsumer(
            new RabbitMqEventConsumer(fanoutConsumerConnection), services.GetRequiredService<IServiceScopeFactory>(),
            Options.Create(new ConnectionFanoutConsumerOptions()), NullLogger<ConnectionFanoutConsumer>.Instance);

        var dispatcherA = new FakeLocalConnectionDispatcher();
        var dispatcherB = new FakeLocalConnectionDispatcher();
        await using var nodeConsumerConnectionA = fixture.CreateRabbitMqConnection();
        await using var nodeConsumerConnectionB = fixture.CreateRabbitMqConnection();
        var nodeConsumerA = new NodeDeliveryConsumer(new RabbitMqEventConsumer(nodeConsumerConnectionA), dispatcherA, nodeA, NullLogger<NodeDeliveryConsumer>.Instance);
        var nodeConsumerB = new NodeDeliveryConsumer(new RabbitMqEventConsumer(nodeConsumerConnectionB), dispatcherB, nodeB, NullLogger<NodeDeliveryConsumer>.Instance);

        await dispatcher.StartAsync(CancellationToken.None);
        await fanoutConsumer.StartAsync(CancellationToken.None);
        await nodeConsumerA.StartAsync(CancellationToken.None);
        await nodeConsumerB.StartAsync(CancellationToken.None);

        // `15-17`: wait for the fact each Competing subscription's own queue actually exists, not a
        // fixed sleep - see WebhookDispatchSharedQueueRegressionTests' own remarks for why StartAsync
        // alone cannot be awaited for this.
        await using var subscriptionProbeConnection = fixture.CreateRabbitMqConnection();
        await RabbitMqSubscriptionTestHelpers.AwaitAllCompetingSubscriptionsAsync(
            subscriptionProbeConnection, TimeSpan.FromSeconds(10),
            (nameof(MessageAccepted), ConnectionFanoutConsumer.ConsumerName),
            (NodeTopics.For(nodeA), RabbitMqSubscriptionTestHelpers.NodeDeliveryConsumerName),
            (NodeTopics.For(nodeB), RabbitMqSubscriptionTestHelpers.NodeDeliveryConsumerName));

        try
        {
            await using var writeDb = fixture.CreateDbContext();
            var handler = new SendVisitorMessageHandler(
                new ConversationRepository(writeDb), new FakeRateLimiter(), new MessageSendRateLimitOptions(),
                new SynchronousMessagePipeline(fixture.DataSource));

            var result = await handler.HandleAsync(new SendVisitorMessage(conversationId, visitorId, "hello from node A"), CancellationToken.None);
            Assert.True(result.IsSuccess);

            var delivered = await OutboxTestHelpers.WaitUntilAsync(
                () => dispatcherB.Dispatches.Count > 0, TimeSpan.FromSeconds(15));
            Assert.True(delivered, "Timed out waiting for the operator's node (B) to receive the delivery.");
        }
        finally
        {
            await dispatcher.StopAsync(CancellationToken.None);
            await fanoutConsumer.StopAsync(CancellationToken.None);
            await nodeConsumerA.StopAsync(CancellationToken.None);
            await nodeConsumerB.StopAsync(CancellationToken.None);
        }

        var receivedByB = Assert.Single(dispatcherB.Dispatches);
        Assert.Equal(operatorConnection, receivedByB.ConnectionId);
        Assert.Equal("MessageReceived", receivedByB.Method);

        // The visitor is a recipient too (nothing excludes the sender's own connection - see
        // VisitorHub.EchoToCallerAsync's comment) - node A's dispatcher gets it via the same real path.
        var receivedByA = Assert.Single(dispatcherA.Dispatches);
        Assert.Equal(visitorConnection, receivedByA.ConnectionId);
    }

    private ServiceProvider BuildServiceProvider(RabbitMqConnection fanoutPublisherConnection)
    {
        var services = new ServiceCollection();
        services.AddDbContext<AgoChatDbContext>(options => options.UseNpgsql(fixture.DataSource));
        services.AddScoped<IConversationRepository, ConversationRepository>();
        services.AddScoped<IConversationReadStore>(_ => new ConversationReadStore(fixture.DataSource));
        services.AddSingleton<IConnectionRegistry>(_ => new RedisConnectionRegistry(
            fixture.RedisMultiplexer, Options.Create(new ConnectionRegistryOptions()), NullLogger<RedisConnectionRegistry>.Instance));
        // Not IAsyncDisposable-registered here: RabbitMqEventPublisher.DisposeAsync only disposes
        // its own channel, never the RabbitMqConnection it was given - that connection's lifetime
        // is the caller's, and the test method's own `await using` on fanoutPublisherConnection owns it.
        services.AddSingleton<IEventPublisher>(_ => new RabbitMqEventPublisher(fanoutPublisherConnection, NullLogger<RabbitMqEventPublisher>.Instance));
        services.AddSingleton<IClock, SystemClock>();
        services.AddSingleton<INodeFanoutPublisher, NodeFanoutPublisher>();
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
