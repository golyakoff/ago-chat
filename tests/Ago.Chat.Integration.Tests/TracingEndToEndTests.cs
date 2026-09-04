using System.Diagnostics;
using Ago.Chat.Application.Abstractions;
using Ago.Chat.Application.Realtime;
using Ago.Chat.Application.UseCases.ResolveMessageDelivery;
using Ago.Chat.Application.UseCases.SendMessage;
using Ago.Chat.Contracts;
using Ago.Chat.Domain;
using Ago.Chat.Infrastructure.Postgres;
using Ago.Chat.Infrastructure.Postgres.Persistence;
using Ago.Chat.Infrastructure.Postgres.Pipeline;
using Ago.Chat.Module.Pipeline;
using Ago.Chat.Worker;
using Ago.Platform.Abstractions;
using Ago.Platform.Hosting;
using Ago.Platform.Kernel;
using Ago.Platform.Messaging.RabbitMq;
using Ago.Platform.Realtime;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using OpenTelemetry;
using OpenTelemetry.Exporter;
using OpenTelemetry.Trace;

namespace Ago.Chat.Integration.Tests;

/// <summary>
/// `7-01`'s own Done-when: sends one message through the real handler chain - hub span (this test's
/// stand-in for `VisitorHub.SendMessageAsync`, since standing up a real SignalR connection just to
/// prove tracing wiring would duplicate `ConnectionFanoutEndToEndTests`' own real-hub-vs-handler-only
/// trade-off for no new signal - the span-starting code under test is the same
/// `ChatTracing.Source.StartActivity` call the real hub method makes) -> `SendVisitorMessageHandler`
/// -> the real channel-based pipeline (`ChannelMessagePipeline`/`MessagePipelineWorkerHost`/
/// `BatchFlusherService`/`MessageBatchWriter`, not `SynchronousMessagePipeline` - the whole point is
/// proving trace context survives the channel hop a synchronous test double would not exercise) -> a
/// real `OutboxDispatcher` -> a real RabbitMQ broker -> a real `ConnectionFanoutConsumer` ->
/// `NodeFanoutPublisher` -> a real `NodeDeliveryConsumer` -> a fake `ILocalConnectionDispatcher` (the
/// one edge `ConnectionFanoutEndToEndTests` also fakes - the SignalR-facing edge `Ago.Chat.Api` owns,
/// genuinely outside this process's own code). Asserts every span an in-memory exporter captured
/// shares one trace id, in the right order by span kind/name - not just a count.
/// </summary>
[Collection(ConnectionFanoutCollection.Name)]
public sealed class TracingEndToEndTests(ConnectionFanoutFixture fixture)
{
    private static readonly DateTimeOffset Now = new(DateTimeOffset.UtcNow.Ticks / TimeSpan.TicksPerSecond * TimeSpan.TicksPerSecond, TimeSpan.Zero);

    [Fact]
    public async Task SendingOneMessage_ProducesOneTraceId_SpanningHubThroughDelivery()
    {
        var exportedActivities = new List<Activity>();
        using var tracerProvider = Sdk.CreateTracerProviderBuilder()
            // The same "Ago.*" wildcard AddPlatformObservability itself subscribes with
            // (Ago.Platform.Hosting's own remarks) - proves the actual subscription shape works,
            // not a hand-picked list of the exact sources this test happens to know about.
            .AddSource(Ago.Platform.Observability.ObservabilityServiceCollectionExtensions.ActivitySourceWildcard)
            .AddSource("Npgsql")
            .AddInMemoryExporter(exportedActivities)
            .Build();

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
        var node = new NodeId($"node-{Guid.NewGuid():N}");
        var operatorConnection = new ConnectionId(Guid.NewGuid().ToString());
        await registry.RegisterAsync(operatorConnection, node, PrincipalKeys.ForOperator(operatorId), CancellationToken.None);

        // The real, channel-based pipeline (concurrency.md's "In-process pipeline (Api)") - the
        // channel hop is exactly what would silently break trace propagation without PendingMessage.
        // TraceParent, MessageBatchWriter's captured-context re-parenting.
        var lifetime = new FakeHostApplicationLifetime();
        var pipelineOptions = Options.Create(new MessagePipelineOptions());
        var pipeline = new ChannelMessagePipeline(pipelineOptions, lifetime);
        var sequencer = new ConversationSequencer();
        var accumulator = new BatchAccumulator();
        var batchWriter = new MessageBatchWriter(fixture.DataSource, new SystemClock(), new UuidV7Generator(), new NoOpCache(), NullLogger<MessageBatchWriter>.Instance);
        var workerHost = new MessagePipelineWorkerHost(pipeline, sequencer, accumulator, pipelineOptions, NullLogger<MessagePipelineWorkerHost>.Instance);
        var flusherService = new BatchFlusherService(accumulator, batchWriter, new SystemClock(), pipelineOptions);

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

        var dispatcherFake = new FakeLocalConnectionDispatcher();
        await using var nodeConsumerConnection = fixture.CreateRabbitMqConnection();
        var nodeConsumer = new NodeDeliveryConsumer(new RabbitMqEventConsumer(nodeConsumerConnection), dispatcherFake, node, NullLogger<NodeDeliveryConsumer>.Instance);

        await workerHost.StartAsync(CancellationToken.None);
        await flusherService.StartAsync(CancellationToken.None);
        await dispatcher.StartAsync(CancellationToken.None);
        await fanoutConsumer.StartAsync(CancellationToken.None);
        await nodeConsumer.StartAsync(CancellationToken.None);

        // `15-17`: wait for the fact each Competing subscription's own queue actually exists, not a
        // fixed sleep - see WebhookDispatchSharedQueueRegressionTests' own remarks for why StartAsync
        // alone cannot be awaited for this. workerHost/flusherService are the in-process channel
        // pipeline (concurrency.md) - no RabbitMQ subscription of their own, so nothing to wait for.
        await using var subscriptionProbeConnection = fixture.CreateRabbitMqConnection();
        await RabbitMqSubscriptionTestHelpers.AwaitAllCompetingSubscriptionsAsync(
            subscriptionProbeConnection, TimeSpan.FromSeconds(10),
            (nameof(MessageAccepted), ConnectionFanoutConsumer.ConsumerName),
            (NodeTopics.For(node), RabbitMqSubscriptionTestHelpers.NodeDeliveryConsumerName));

        string? hubTraceId;
        try
        {
            await using var writeDb = fixture.CreateDbContext();
            var handler = new SendVisitorMessageHandler(
                new ConversationRepository(writeDb), new FakeRateLimiter(), new MessageSendRateLimitOptions(), pipeline);

            // The test's own stand-in for VisitorHub.SendMessageAsync - the exact
            // ChatTracing.Source.StartActivity call the real hub method makes, see this class's own
            // remarks for why a real SignalR connection is not stood up just to prove this.
            using (var hubActivity = ChatTracing.Source.StartActivity(ChatTracing.SpanNames.HubSendMessage, ActivityKind.Server))
            {
                hubTraceId = hubActivity?.TraceId.ToString();
                Assert.NotNull(hubTraceId);

                var result = await handler.HandleAsync(
                    new SendVisitorMessage(conversationId, visitorId, "hello, traced world", TraceParent: hubActivity?.Id),
                    CancellationToken.None);
                Assert.True(result.IsSuccess, result.IsFailure ? result.Error!.Value.Message : null);
            }

            var delivered = await OutboxTestHelpers.WaitUntilAsync(() => dispatcherFake.Dispatches.Count > 0, TimeSpan.FromSeconds(15));
            Assert.True(delivered, "Timed out waiting for the operator's node to receive the delivery.");
        }
        finally
        {
            lifetime.TriggerStopping(); // completes ChannelMessagePipeline's inbound channel - see its own constructor remarks
            await workerHost.StopAsync(CancellationToken.None);
            await flusherService.StopAsync(CancellationToken.None);
            await dispatcher.StopAsync(CancellationToken.None);
            await fanoutConsumer.StopAsync(CancellationToken.None);
            await nodeConsumer.StopAsync(CancellationToken.None);
        }

        tracerProvider.ForceFlush();

        Assert.Single(dispatcherFake.Dispatches);
        Assert.NotNull(hubTraceId);

        // `Npgsql` also emits a span for every *other* query this test runs (seeding site/visitor/
        // operator, the handler's own conversation lookup, connection-registry reads) - none of
        // those belong to this message's own trace, and asserting the whole process emitted exactly
        // one trace id for its entire run would be a false, untestable claim no real system meets.
        // nfr.md's actual promise is narrower and is what this asserts: follow *this message's own*
        // trace id and every stage of its pipeline is on it, one causally-linked trace, not five
        // separate ones reconstructed by hand from timestamps.
        var spansInTrace = exportedActivities.Where(a => a.TraceId.ToString() == hubTraceId).ToList();

        // Every stage nfr.md names, present by name/kind, exactly once, inside this one trace - not
        // just counted, and not merely "exists somewhere in the whole test run."
        var hub = SingleSpanInTrace(ChatTracing.SpanNames.HubSendMessage, ActivityKind.Server);
        var persist = SingleSpanInTrace(ChatTracing.SpanNames.PipelinePersistBatch, ActivityKind.Internal);
        var outboxDispatch = SingleSpanInTrace(ChatTracing.SpanNames.OutboxDispatch, ActivityKind.Producer);
        var consumed = SingleSpanInTrace($"{nameof(MessageAccepted)} process", ActivityKind.Consumer); // ConnectionFanoutConsumer's own topic
        var delivery = SingleSpanInTrace("node_delivery.dispatch_to_connection", ActivityKind.Producer);

        // Order: each stage genuinely happens after the one before it, by wall-clock start time -
        // proves causal sequencing, not merely "these spans exist somewhere in this trace."
        Assert.True(hub.StartTimeUtc <= persist.StartTimeUtc, "DB write should start no earlier than the hub span.");
        Assert.True(persist.StartTimeUtc <= outboxDispatch.StartTimeUtc, "Outbox dispatch should start no earlier than the DB write.");
        Assert.True(outboxDispatch.StartTimeUtc <= consumed.StartTimeUtc, "Broker consume should start no earlier than the outbox dispatch.");
        Assert.True(consumed.StartTimeUtc <= delivery.StartTimeUtc, "Delivery should start no earlier than the broker consume.");

        // Real parent-child edges, not merely "somewhere in the same trace": the outbox dispatch is
        // a direct child of the hub span specifically (MessageBatchWriter's own remarks on why),
        // and the broker consume is a direct child of the outbox dispatch (RabbitMqTracing's
        // header-based propagation, Ago.Platform.Messaging.RabbitMq).
        Assert.Equal(hub.SpanId, outboxDispatch.ParentSpanId);
        Assert.Equal(outboxDispatch.SpanId, consumed.ParentSpanId);

        Activity SingleSpanInTrace(string name, ActivityKind kind) => Assert.Single(
            spansInTrace, a => a.OperationName == name && a.Kind == kind);
    }

    private ServiceProvider BuildServiceProvider(RabbitMqConnection fanoutPublisherConnection)
    {
        var services = new ServiceCollection();
        services.AddDbContext<AgoChatDbContext>(options => options.UseNpgsql(fixture.DataSource));
        services.AddScoped<IConversationRepository, ConversationRepository>();
        services.AddScoped<IConversationReadStore>(_ => new ConversationReadStore(fixture.DataSource));
        services.AddSingleton<IConnectionRegistry>(_ => new RedisConnectionRegistry(
            fixture.RedisMultiplexer, Options.Create(new ConnectionRegistryOptions()), NullLogger<RedisConnectionRegistry>.Instance));
        services.AddSingleton<IEventPublisher>(_ => new RabbitMqEventPublisher(fanoutPublisherConnection, NullLogger<RabbitMqEventPublisher>.Instance));
        services.AddSingleton<IClock, SystemClock>();
        services.AddSingleton<INodeFanoutPublisher, NodeFanoutPublisher>();
        services.AddScoped<ResolveMessageDeliveryTargetsHandler>();
        return services.BuildServiceProvider();
    }

    private sealed class FakeLocalConnectionDispatcher : ILocalConnectionDispatcher
    {
        public System.Collections.Concurrent.ConcurrentBag<(ConnectionId ConnectionId, string Method, string PayloadJson)> Dispatches { get; } = [];

        public Task<DispatchOutcome> DispatchAsync(ConnectionId connectionId, string method, string payloadJson, CancellationToken cancellationToken)
        {
            Dispatches.Add((connectionId, method, payloadJson));
            return Task.FromResult(DispatchOutcome.Delivered);
        }
    }

    /// <summary>Same minimal shape as `Ago.Chat.Concurrency.Tests.FakeHostApplicationLifetime` -
    /// duplicated rather than shared across test projects for the same reason
    /// `ConnectionFanoutEndToEndTests` hand-builds its own fakes instead of a shared test-utilities
    /// project (small enough that a new dependency edge between two test projects is not worth it).</summary>
    private sealed class FakeHostApplicationLifetime : IHostApplicationLifetime
    {
        private readonly CancellationTokenSource _stopping = new();

        public CancellationToken ApplicationStarted => CancellationToken.None;

        public CancellationToken ApplicationStopping => _stopping.Token;

        public CancellationToken ApplicationStopped => CancellationToken.None;

        public void TriggerStopping() => _stopping.Cancel();

        public void StopApplication() => TriggerStopping();
    }
}
