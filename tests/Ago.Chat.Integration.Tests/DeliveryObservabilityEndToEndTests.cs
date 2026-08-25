using System.Collections.Concurrent;
using Ago.Platform.Observability;
using System.Diagnostics;
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
using Ago.Platform.Realtime;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

namespace Ago.Chat.Integration.Tests;

/// <summary>
/// `7-08`'s last Done-when, made repeatable: the question that started the item - "did the server
/// even try to deliver that message to the operator's connection?" - answered from telemetry alone,
/// with nothing read out of the fakes to answer it.
///
/// The scenario is the incident's own shape (2026-08-25): a visitor and an assigned operator, a
/// message the visitor sends, the visitor's console receiving it, the operator's not. The operator's
/// connection is registered in Redis on node B, but node B no longer holds it - the stale entry
/// realtime.md calls "advice, not truth" - so the delivery is a harmless no-op, exactly as designed,
/// and exactly as invisible as it used to be.
///
/// Before this item, every observable fact about that run was identical to a run where the operator
/// received the message. The three assertions at the bottom are the difference, and they are the
/// answer: the operator *was* a resolved recipient, the registry *did* believe them present, node B
/// *was* asked, and node B is where it stopped.
/// </summary>
[Collection(ConnectionFanoutCollection.Name)]
public sealed class DeliveryObservabilityEndToEndTests(ConnectionFanoutFixture fixture)
{
    private static readonly DateTimeOffset Now = new(DateTimeOffset.UtcNow.Ticks / TimeSpan.TicksPerSecond * TimeSpan.TicksPerSecond, TimeSpan.Zero);

    [Fact]
    public async Task AMessageTheOperatorNeverSaw_LeavesEnoughBehindToSayWhereItStopped()
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
        await registry.RegisterAsync(new ConnectionId(Guid.NewGuid().ToString()), nodeA, PrincipalKeys.ForVisitor(visitorId), CancellationToken.None);
        await registry.RegisterAsync(new ConnectionId(Guid.NewGuid().ToString()), nodeB, PrincipalKeys.ForOperator(operatorId), CancellationToken.None);

        var exportedActivities = new List<Activity>();
        using var tracerProvider = Sdk.CreateTracerProviderBuilder()
            .AddSource(Ago.Platform.Observability.ObservabilityServiceCollectionExtensions.ActivitySourceWildcard)
            .AddInMemoryExporter(exportedActivities)
            .Build();
        var exportedMetrics = new List<Metric>();
        using var meterProvider = Sdk.CreateMeterProviderBuilder()
            .AddMeter(ChatMetrics.MeterName)
            .AddMeter(RealtimeMetrics.MeterName)
            .AddInMemoryExporter(exportedMetrics)
            .Build();

        await using var fanoutPublisherConnection = fixture.CreateRabbitMqConnection();
        await using var services = BuildServiceProvider(fanoutPublisherConnection);

        await using var dispatcherConnection = fixture.CreateRabbitMqConnection();
        var outboxDispatcher = new OutboxDispatcher(
            fixture.DataSource, new RabbitMqEventPublisher(dispatcherConnection), new SystemClock(),
            Options.Create(new OutboxDispatcherOptions { PollInterval = TimeSpan.FromSeconds(2) }), NullLogger<OutboxDispatcher>.Instance);

        await using var fanoutConsumerConnection = fixture.CreateRabbitMqConnection();
        var fanoutConsumer = new ConnectionFanoutConsumer(
            new RabbitMqEventConsumer(fanoutConsumerConnection), services.GetRequiredService<IServiceScopeFactory>(),
            Options.Create(new ConnectionFanoutConsumerOptions()), NullLogger<ConnectionFanoutConsumer>.Instance);

        // Node A still holds the visitor's connection. Node B does not hold the operator's any more -
        // the console was closed without a clean unregister, so Redis still lists it until the TTL
        // lapses. That is the whole incident, reproduced.
        var dispatcherA = new OutcomeReportingDispatcher(DispatchOutcome.Delivered);
        var dispatcherB = new OutcomeReportingDispatcher(DispatchOutcome.ConnectionNotLocal);
        await using var nodeConsumerConnectionA = fixture.CreateRabbitMqConnection();
        await using var nodeConsumerConnectionB = fixture.CreateRabbitMqConnection();
        var nodeConsumerA = new NodeDeliveryConsumer(new RabbitMqEventConsumer(nodeConsumerConnectionA), dispatcherA, nodeA, NullLogger<NodeDeliveryConsumer>.Instance);
        var nodeConsumerB = new NodeDeliveryConsumer(new RabbitMqEventConsumer(nodeConsumerConnectionB), dispatcherB, nodeB, NullLogger<NodeDeliveryConsumer>.Instance);

        await outboxDispatcher.StartAsync(CancellationToken.None);
        await fanoutConsumer.StartAsync(CancellationToken.None);
        await nodeConsumerA.StartAsync(CancellationToken.None);
        await nodeConsumerB.StartAsync(CancellationToken.None);
        await Task.Delay(TimeSpan.FromMilliseconds(500)); // subscriptions to actually land - see NodeFanoutTests

        try
        {
            await using var writeDb = fixture.CreateDbContext();
            var handler = new SendVisitorMessageHandler(
                new ConversationRepository(writeDb), new FakeRateLimiter(), new MessageSendRateLimitOptions(),
                new SynchronousMessagePipeline(fixture.DataSource));

            var result = await handler.HandleAsync(new SendVisitorMessage(conversationId, visitorId, "are you there?"), CancellationToken.None);
            Assert.True(result.IsSuccess);

            var bothNodesAsked = await OutboxTestHelpers.WaitUntilAsync(
                () => dispatcherA.Calls.Count > 0 && dispatcherB.Calls.Count > 0, TimeSpan.FromSeconds(15));
            Assert.True(bothNodesAsked, "Timed out waiting for both nodes to be asked to deliver.");
        }
        finally
        {
            await outboxDispatcher.StopAsync(CancellationToken.None);
            await fanoutConsumer.StopAsync(CancellationToken.None);
            await nodeConsumerA.StopAsync(CancellationToken.None);
            await nodeConsumerB.StopAsync(CancellationToken.None);
        }

        // ---- the answer, read only from telemetry ----

        // 1. How many people was this message meant for, and how many live connections did the
        //    registry find for them? Two and two - so the operator was not silently dropped from the
        //    recipient list, which is the first thing the incident had no way to rule out.
        tracerProvider.ForceFlush();
        var fanoutSpan = Assert.Single(exportedActivities, activity => activity.GetTagItem("ago.fanout.recipients") is not null);
        Assert.Equal(2, fanoutSpan.GetTagItem("ago.fanout.recipients"));
        Assert.Equal(2, fanoutSpan.GetTagItem("ago.fanout.connections"));
        Assert.Equal(2, fanoutSpan.GetTagItem("ago.fanout.nodes"));

        meterProvider.ForceFlush();
        var recipients = exportedMetrics.Single(m => m.Name == ChatMetrics.DeliveryRecipientsInstrumentName);
        var dispatches = exportedMetrics.Single(m => m.Name == RealtimeMetrics.DispatchesInstrumentName);

        // 2. Which kind of principal was each, and was it believed present? An operator under
        //    `connected` is the fact that rules the recipient list out as the cause.
        Assert.Equal(1, SumRecipients(recipients, PrincipalKeys.OperatorKind, ChatMetrics.ConnectedPresence));
        Assert.Equal(1, SumRecipients(recipients, PrincipalKeys.VisitorKind, ChatMetrics.ConnectedPresence));
        Assert.Equal(0, SumRecipients(recipients, PrincipalKeys.OperatorKind, ChatMetrics.AbsentPresence));

        // 3. And did the server try? Yes - node B was asked, and reported it no longer held that
        //    connection, while node A delivered the visitor's copy from the same fan-out. Everything
        //    before this hop worked; the connection is where it stopped.
        Assert.Equal(1, SumDispatches(dispatches, nodeB, RealtimeMetrics.ConnectionNotLocalOutcome));
        Assert.Equal(0, SumDispatches(dispatches, nodeB, RealtimeMetrics.DeliveredOutcome));
        Assert.Equal(1, SumDispatches(dispatches, nodeA, RealtimeMetrics.DeliveredOutcome));
    }

    private static long SumRecipients(Metric metric, string recipientKind, string presence) =>
        SumMatching(
            metric,
            ("method", "MessageReceived"),
            ("recipient_kind", recipientKind),
            ("presence", presence));

    private static long SumDispatches(Metric metric, NodeId node, string outcome) =>
        SumMatching(metric, ("node", node.Value), ("outcome", outcome));

    private static long SumMatching(Metric metric, params (string Key, string Value)[] required)
    {
        var total = 0L;
        foreach (ref readonly var point in metric.GetMetricPoints())
        {
            var matched = 0;
            foreach (var tag in point.Tags)
            {
                foreach (var (key, value) in required)
                {
                    if (tag.Key == key && (string?)tag.Value == value)
                    {
                        matched++;
                    }
                }
            }

            if (matched == required.Length)
            {
                total += point.GetSumLong();
            }
        }

        return total;
    }

    private ServiceProvider BuildServiceProvider(RabbitMqConnection fanoutPublisherConnection)
    {
        var services = new ServiceCollection();
        services.AddDbContext<AgoChatDbContext>(options => options.UseNpgsql(fixture.DataSource));
        services.AddScoped<IConversationRepository, ConversationRepository>();
        services.AddScoped<IConversationReadStore>(_ => new ConversationReadStore(fixture.DataSource));
        services.AddSingleton<IConnectionRegistry>(_ => new RedisConnectionRegistry(
            fixture.RedisMultiplexer, Options.Create(new ConnectionRegistryOptions()), NullLogger<RedisConnectionRegistry>.Instance));
        services.AddSingleton<IEventPublisher>(_ => new RabbitMqEventPublisher(fanoutPublisherConnection));
        services.AddSingleton<IClock, SystemClock>();
        services.AddSingleton<INodeFanoutPublisher, NodeFanoutPublisher>();
        services.AddScoped<ResolveMessageDeliveryTargetsHandler>();
        return services.BuildServiceProvider();
    }

    /// <summary>Stands in for `Ago.Chat.Api`'s <c>SignalRConnectionDispatcher</c>, which reports
    /// <see cref="DispatchOutcome.ConnectionNotLocal"/> for exactly the case this test needs: a
    /// connection its <c>LocalConnectionTracker</c> no longer knows about.</summary>
    private sealed class OutcomeReportingDispatcher(DispatchOutcome outcome) : ILocalConnectionDispatcher
    {
        public ConcurrentBag<ConnectionId> Calls { get; } = [];

        public Task<DispatchOutcome> DispatchAsync(ConnectionId connectionId, string method, string payloadJson, CancellationToken cancellationToken)
        {
            Calls.Add(connectionId);
            return Task.FromResult(outcome);
        }
    }
}
