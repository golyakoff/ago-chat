using System.Collections.Concurrent;
using Ago.Chat.Application.UseCases.ResolveConversationAssignment;
using Ago.Chat.Contracts;
using Ago.Chat.Domain;
using Ago.Chat.FakeCrm.Tests;
using Ago.Chat.Webhooks;
using Ago.Chat.Worker;
using Ago.Platform.Abstractions;
using Ago.Platform.Messaging.RabbitMq;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Ago.Chat.Integration.Tests;

/// <summary>
/// `6-05`'s own Done-when: "5-11 (the shared-queue Competing-consumer bug) confirmed fixed and this
/// item's own two consumers pass its regression test too, not just inherit the fix by luck." This item
/// makes `ConversationAssignedToOperator` a *second* live instance of the exact shape `5-11` fixed once
/// already (`RabbitMqPublishConsumeTests.Competing_TwoDifferentConsumerTypes_BothReceiveEveryMessageIndependently`,
/// `ago-platform`) - `Ago.Chat.Worker.ConversationAssignmentFanoutConsumer` (`4-02`) and this item's own
/// new `ConversationAssignmentWebhookDispatchConsumer` both subscribe `Competing` to the same topic.
/// If either ever silently reused the other's queue name (or the platform's own fix regressed), N
/// published messages would split between the two consumer types instead of each independently
/// receiving all N - this test publishes N and asserts both totals equal N, not that they sum to N.
/// </summary>
[Collection(WebhookDispatchCollection.Name)]
public sealed class WebhookDispatchSharedQueueRegressionTests(WebhookDispatchFixture fixture)
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
    private const int MessageCount = 6;

    [Fact]
    public async Task BothConsumerTypes_EachReceiveEveryMessage_NeitherSplitsWithTheOther()
    {
        await using var crm = new FakeCrmProcessFixture { DefaultBehavior = "succeeds" };
        await crm.InitializeAsync();

        await using var seedDb = fixture.CreateDbContext();
        var siteId = await WebhookDispatchTestHarness.SeedSiteAsync(seedDb);
        var visitorId = new VisitorId(Guid.NewGuid());
        var operatorId = new OperatorId(Guid.NewGuid());
        seedDb.Visitors.Add(new Visitor(visitorId, siteId, Now));
        seedDb.Operators.Add(new Operator(operatorId, siteId, OperatorStatus.Online, capacity: 5));
        await seedDb.SaveChangesAsync();
        var endpoint = await WebhookDispatchTestHarness.RegisterEndpointAsync(
            seedDb, siteId, new Uri(crm.BaseAddress, "webhooks/deliver"), Now);

        // `15-17`: this test is not WebhookDispatchBreakerTests - its own subject is the shared-queue
        // fix, not breaker behaviour - so BreakDuration is deliberately far shorter than the wait
        // below rather than left at the 30s production default. All six messages go to the same
        // endpoint, and GetEndpointPipeline's breaker is keyed by WebhookEndpointId (shared across
        // all six), so a single slow call under CI load is enough to open it (MinimumThroughput: 2)
        // - a spurious trip must self-heal well inside DeliveryWait, not be waited out at 1:1 odds.
        var deliveryWait = TimeSpan.FromSeconds(20);
        var resilienceOptions = WebhookDispatchTestHarness.ResilienceOptions(breakDuration: TimeSpan.FromSeconds(2));
        WebhookDispatchTestHarness.AssertBreakDurationFitsWithinWait(resilienceOptions, deliveryWait);

        // The webhook-dispatch consumer, real end to end (real Postgres write, real signed HTTP
        // call to the real FakeCrm process) - the same stack WebhookDispatchIdempotencyTests uses.
        var (webhookServices, _) = WebhookDispatchTestHarness.BuildConsumerServices(
            fixture, resilienceOptions, WebhookDispatchTestHarness.HttpOptions());
        await using var webhookServicesDisposable = webhookServices;
        await using var webhookConsumerConnection = fixture.CreateRabbitMqConnection();
        var webhookConsumer = new ConversationAssignmentWebhookDispatchConsumer(
            new RabbitMqEventConsumer(webhookConsumerConnection), webhookServices.GetRequiredService<IServiceScopeFactory>(),
            Options.Create(new ConversationAssignmentWebhookDispatchConsumerOptions()),
            NullLogger<ConversationAssignmentWebhookDispatchConsumer>.Instance);

        // The pre-existing (4-02) fanout consumer - a fake INodeFanoutPublisher is enough here:
        // this test proves message *delivery to the consumer type*, the same property
        // ConversationAssignmentFanoutEndToEndTests already proves for the realtime fan-out itself
        // in full, so re-standing up Redis/the connection registry here would only duplicate that
        // proof, not add to this one.
        var fanoutPublisher = new RecordingNodeFanoutPublisher();
        var fanoutServices = new ServiceCollection();
        fanoutServices.AddSingleton<INodeFanoutPublisher>(fanoutPublisher);
        fanoutServices.AddScoped<ResolveConversationAssignmentTargetsHandler>();
        await using var fanoutServiceProvider = fanoutServices.BuildServiceProvider();
        await using var fanoutConsumerConnection = fixture.CreateRabbitMqConnection();
        var fanoutConsumer = new ConversationAssignmentFanoutConsumer(
            new RabbitMqEventConsumer(fanoutConsumerConnection), fanoutServiceProvider.GetRequiredService<IServiceScopeFactory>(),
            Options.Create(new ConversationAssignmentFanoutConsumerOptions()),
            NullLogger<ConversationAssignmentFanoutConsumer>.Instance);

        await webhookConsumer.StartAsync(CancellationToken.None);
        await fanoutConsumer.StartAsync(CancellationToken.None);
        try
        {
            // `15-17`: wait for the fact each Competing subscription's own queue has a live consumer
            // attached, not merely that the queue exists - awaiting StartAsync only awaits
            // BackgroundService.StartAsync's synchronous kickoff, never the SubscribeAsync work it
            // starts running in the background (.NET's own BackgroundService never awaits the task it
            // hands to ExecuteAsync). A prior version of this fix checked queue existence via a
            // passive AMQP declare, which succeeds as soon as the queue is declared - before it is
            // bound - and still lost publishes into the declare-to-bind window under real CI load
            // (golyakoff/ago-chat/actions/runs/33839119087); RabbitMqSubscriptionTestHelpers' own
            // remarks cover why the consumer count is the right fact instead.
            using var subscriptionManagementClient = fixture.CreateRabbitMqManagementClient();
            var webhookSubscriptionLanded = await RabbitMqSubscriptionTestHelpers.WaitForCompetingSubscriptionAsync(
                subscriptionManagementClient, nameof(ConversationAssignedToOperator),
                ConversationAssignmentWebhookDispatchConsumer.ConsumerName, TimeSpan.FromSeconds(10));
            Assert.True(webhookSubscriptionLanded,
                $"The '{ConversationAssignmentWebhookDispatchConsumer.ConsumerName}' subscription to " +
                $"'{nameof(ConversationAssignedToOperator)}' never landed - queue " +
                $"'{RabbitMqSubscriptionTestHelpers.CompetingQueueName(nameof(ConversationAssignedToOperator), ConversationAssignmentWebhookDispatchConsumer.ConsumerName)}' " +
                "never reached a live consumer within 10s.");
            var fanoutSubscriptionLanded = await RabbitMqSubscriptionTestHelpers.WaitForCompetingSubscriptionAsync(
                subscriptionManagementClient, nameof(ConversationAssignedToOperator),
                ConversationAssignmentFanoutConsumer.ConsumerName, TimeSpan.FromSeconds(10));
            Assert.True(fanoutSubscriptionLanded,
                $"The '{ConversationAssignmentFanoutConsumer.ConsumerName}' subscription to " +
                $"'{nameof(ConversationAssignedToOperator)}' never landed - queue " +
                $"'{RabbitMqSubscriptionTestHelpers.CompetingQueueName(nameof(ConversationAssignedToOperator), ConversationAssignmentFanoutConsumer.ConsumerName)}' " +
                "never reached a live consumer within 10s.");

            await using var publisherConnection = fixture.CreateRabbitMqConnection();
            var publisher = new RabbitMqEventPublisher(publisherConnection, NullLogger<RabbitMqEventPublisher>.Instance);
            for (var i = 0; i < MessageCount; i++)
            {
                await publisher.PublishAsync(BuildEnvelope(siteId, visitorId, operatorId), CancellationToken.None);
            }

            var webhookCaughtUp = await OutboxTestHelpers.WaitUntilAsync(
                () =>
                {
                    using var scope = webhookServices.CreateScope();
                    var db = scope.ServiceProvider.GetRequiredService<Ago.Chat.Infrastructure.Postgres.Persistence.AgoChatDbContext>();
                    return db.WebhookDeliveries.Count(d => d.EndpointId == endpoint.Id) >= MessageCount;
                },
                deliveryWait);
            var fanoutCaughtUp = await OutboxTestHelpers.WaitUntilAsync(
                () => fanoutPublisher.Calls.Count >= MessageCount, deliveryWait);

            // Three different findings this assertion must not collapse into one wording (the CI
            // failure this item fixes produced both, on separate runs, of what used to be one shared
            // message describing neither): 0 received is a lost publish (subscription raced ahead of
            // the publish - defect `15-17` fixes above); a partial, non-split count is what the wait
            // simply timing out looks like (e.g. a breaker still recovering); a split total *less
            // than* MessageCount *summed across both* consumer types is the actual shared-queue bug
            // this test exists to catch.
            var webhookReceived = AwaitDeliveryCount(fixture, endpoint.Id);
            Assert.True(webhookCaughtUp, DescribeShortfall("Webhook-dispatch", webhookReceived, fanoutPublisher.Calls.Count));
            Assert.True(fanoutCaughtUp, DescribeShortfall("Fanout", fanoutPublisher.Calls.Count, webhookReceived));
        }
        finally
        {
            await webhookConsumer.StopAsync(CancellationToken.None);
            await fanoutConsumer.StopAsync(CancellationToken.None);
        }

        // Each consumer type received every message independently - not summing to MessageCount
        // between the two, which is exactly what the pre-5-11 bug would have produced.
        Assert.Equal(MessageCount, AwaitDeliveryCount(fixture, endpoint.Id));
        Assert.Equal(MessageCount, fanoutPublisher.Calls.Count);
    }

    private static int AwaitDeliveryCount(WebhookDispatchFixture fixture, WebhookEndpointId endpointId)
    {
        using var db = fixture.CreateDbContext();
        return db.WebhookDeliveries.Count(d => d.EndpointId == endpointId);
    }

    /// <summary>`15-17`: the CI failure this item fixes produced two different shapes on two
    /// different runs of the same commit (0/6, then 5/6 on a re-run) - and the assertion message this
    /// replaced ("the shared-queue bug would show up as a split total less than 6") described
    /// neither. This distinguishes all three findings a shortfall here can actually mean, so the next
    /// person reading a failure is pointed at the right one instead of at the property this test was
    /// written to prove.</summary>
    private static string DescribeShortfall(string consumerLabel, int received, int otherReceived)
    {
        if (received == 0)
        {
            return $"{consumerLabel} consumer received 0/{MessageCount} messages - a lost publish (the exchange had no " +
                   "queue bound to it yet when the messages were published, so RabbitMQ discarded them), not the " +
                   "shared-queue bug this test exists to catch.";
        }

        if (received + otherReceived == MessageCount)
        {
            return $"{consumerLabel} consumer received {received}/{MessageCount} messages, and the other consumer type " +
                   $"received {otherReceived} - together summing to exactly {MessageCount} rather than each independently " +
                   "receiving every message. This is the shared-queue bug this test exists to catch: the two consumer " +
                   "types split one queue instead of each holding its own.";
        }

        return $"{consumerLabel} consumer received only {received}/{MessageCount} messages within the wait (the two " +
               "counts do not sum to the published total, so this is not a split either) - most likely a still-recovering " +
               "circuit breaker or a slow delivery, not the shared-queue bug this test exists to catch.";
    }

    private static EventEnvelope BuildEnvelope(SiteId siteId, VisitorId visitorId, OperatorId operatorId)
    {
        var contract = new ConversationAssignedToOperator(
            ConversationId: Guid.NewGuid(), SiteId: siteId.Value, VisitorId: visitorId.Value, OperatorId: operatorId.Value,
            CorrelationId: Guid.NewGuid(), OccurredAt: Now);

        return new EventEnvelope(
            MessageId: Guid.NewGuid(),
            Type: nameof(ConversationAssignedToOperator),
            Version: 1,
            PartitionKey: contract.ConversationId.ToString(),
            OccurredAt: Now,
            CorrelationId: contract.CorrelationId,
            Payload: System.Text.Json.JsonSerializer.Serialize(contract));
    }

    private sealed class RecordingNodeFanoutPublisher : INodeFanoutPublisher
    {
        public ConcurrentBag<string> Calls { get; } = [];

        public Task<FanoutResult> PublishAsync(
            IReadOnlyCollection<PrincipalKey> recipients, string method, string payloadJson, Guid correlationId,
            CancellationToken cancellationToken)
        {
            Calls.Add(payloadJson);
            // Nobody connected - this test is about which queue the webhook consumer binds to, not
            // about who a fan-out reached.
            return Task.FromResult(new FanoutResult(
                [.. recipients.Select(recipient => new ResolvedRecipient(recipient, 0))]));
        }
    }
}
