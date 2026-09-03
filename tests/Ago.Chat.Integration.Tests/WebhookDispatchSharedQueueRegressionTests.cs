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

        // The webhook-dispatch consumer, real end to end (real Postgres write, real signed HTTP
        // call to the real FakeCrm process) - the same stack WebhookDispatchIdempotencyTests uses.
        var (webhookServices, _) = WebhookDispatchTestHarness.BuildConsumerServices(
            fixture, WebhookDispatchTestHarness.ResilienceOptions(), WebhookDispatchTestHarness.HttpOptions());
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
            await Task.Delay(TimeSpan.FromMilliseconds(500)); // both subscriptions to actually land

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
                TimeSpan.FromSeconds(20));
            var fanoutCaughtUp = await OutboxTestHelpers.WaitUntilAsync(
                () => fanoutPublisher.Calls.Count >= MessageCount, TimeSpan.FromSeconds(20));

            Assert.True(webhookCaughtUp, $"Webhook-dispatch consumer only received {AwaitDeliveryCount(fixture, endpoint.Id)}/{MessageCount} messages - the shared-queue bug would show up as a split total less than {MessageCount}.");
            Assert.True(fanoutCaughtUp, $"Fanout consumer only received {fanoutPublisher.Calls.Count}/{MessageCount} messages - the shared-queue bug would show up as a split total less than {MessageCount}.");
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
