using System.Collections.Concurrent;
using Ago.Chat.Application.Realtime;
using Ago.Chat.Application.UseCases.ResolveConversationAssignment;
using Ago.Chat.Contracts;
using Ago.Chat.Domain;
using Ago.Chat.Infrastructure.Postgres.Persistence;
using Ago.Chat.Worker;
using Ago.Platform.Abstractions;
using Ago.Platform.Hosting;
using Ago.Platform.Kernel;
using Ago.Platform.Messaging.RabbitMq;
using Ago.Platform.Realtime;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Ago.Chat.Integration.Tests;

/// <summary>
/// `4-02`'s Done-when: both the visitor and the newly-assigned operator receive
/// `"ConversationAssigned"` through the real handler chain -
/// `ConversationAssignmentJob` -&gt; outbox -&gt; `OutboxDispatcher` -&gt;
/// `ConversationAssignedToOperator` -&gt; `ConversationAssignmentFanoutConsumer` -&gt;
/// `ResolveConversationAssignmentTargetsHandler` -&gt; `NodeFanoutPublisher` - not asserted from the
/// domain event alone. Same structural proof as `ConnectionFanoutEndToEndTests` (3-02), same real
/// Postgres/RabbitMQ/Redis, a hand-written fake only for `ILocalConnectionDispatcher`.
/// </summary>
[Collection(ConnectionFanoutCollection.Name)]
public sealed class ConversationAssignmentFanoutEndToEndTests(ConnectionFanoutFixture fixture)
{
    private static readonly DateTimeOffset Now = new(DateTimeOffset.UtcNow.Ticks / TimeSpan.TicksPerSecond * TimeSpan.TicksPerSecond, TimeSpan.Zero);

    [Fact]
    public async Task WaitingConversation_AssignedByTheJob_NotifiesBothTheVisitorsAndTheOperatorsNode()
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
            seed.Conversations.Add(Conversation.Start(conversationId, siteId, visitorId, Now));
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
        var fanoutConsumer = new ConversationAssignmentFanoutConsumer(
            new RabbitMqEventConsumer(fanoutConsumerConnection), services.GetRequiredService<IServiceScopeFactory>(),
            Options.Create(new ConversationAssignmentFanoutConsumerOptions()), NullLogger<ConversationAssignmentFanoutConsumer>.Instance);

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

        // `15-17`: wait for the fact each Competing subscription's own queue has a live consumer
        // attached, not merely that the queue exists - see WebhookDispatchSharedQueueRegressionTests'
        // own remarks for why StartAsync alone cannot be awaited for this, and
        // RabbitMqSubscriptionTestHelpers' own remarks for why "the queue exists" is not enough.
        using var subscriptionManagementClient = fixture.CreateRabbitMqManagementClient();
        await RabbitMqSubscriptionTestHelpers.AwaitAllCompetingSubscriptionsAsync(
            subscriptionManagementClient, TimeSpan.FromSeconds(10),
            (nameof(ConversationAssignedToOperator), ConversationAssignmentFanoutConsumer.ConsumerName),
            (NodeTopics.For(nodeA), RabbitMqSubscriptionTestHelpers.NodeDeliveryConsumerName),
            (NodeTopics.For(nodeB), RabbitMqSubscriptionTestHelpers.NodeDeliveryConsumerName));

        try
        {
            var job = new ConversationAssignmentJob(
                fixture.DataSource,
                new SkipLockedAssignmentClaimer(fixture.DataSource, new SystemClock(), new UuidV7Generator()),
                Options.Create(new ConversationAssignmentJobOptions()), NullLogger<ConversationAssignmentJob>.Instance);
            await job.RunOnceAsync(CancellationToken.None);

            var delivered = await OutboxTestHelpers.WaitUntilAsync(
                () => dispatcherB.Dispatches.Count > 0, TimeSpan.FromSeconds(15));
            Assert.True(delivered, "Timed out waiting for the operator's node (B) to receive the assignment.");
            var deliveredToA = await OutboxTestHelpers.WaitUntilAsync(
                () => dispatcherA.Dispatches.Count > 0, TimeSpan.FromSeconds(15));
            Assert.True(deliveredToA, "Timed out waiting for the visitor's node (A) to receive the assignment.");
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
        Assert.Equal("ConversationAssigned", receivedByB.Method);

        var receivedByA = Assert.Single(dispatcherA.Dispatches);
        Assert.Equal(visitorConnection, receivedByA.ConnectionId);
        Assert.Equal("ConversationAssigned", receivedByA.Method);

        await using var verify = fixture.CreateDbContext();
        var conversation = await verify.Conversations.FindAsync(conversationId);
        Assert.Equal(ConversationState.Assigned, conversation!.State);
        Assert.Equal(operatorId, conversation.OperatorId);
    }

    private ServiceProvider BuildServiceProvider(RabbitMqConnection fanoutPublisherConnection)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IConnectionRegistry>(_ => new RedisConnectionRegistry(
            fixture.RedisMultiplexer, Options.Create(new ConnectionRegistryOptions()), NullLogger<RedisConnectionRegistry>.Instance));
        // Not IAsyncDisposable-registered here: RabbitMqEventPublisher.DisposeAsync only disposes
        // its own channel, never the RabbitMqConnection it was given - that connection's lifetime
        // is the caller's, and the test method's own `await using` on fanoutPublisherConnection owns it.
        services.AddSingleton<IEventPublisher>(_ => new RabbitMqEventPublisher(fanoutPublisherConnection, NullLogger<RabbitMqEventPublisher>.Instance));
        services.AddSingleton<IClock, SystemClock>();
        services.AddSingleton<INodeFanoutPublisher, NodeFanoutPublisher>();
        services.AddScoped<ResolveConversationAssignmentTargetsHandler>();
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
