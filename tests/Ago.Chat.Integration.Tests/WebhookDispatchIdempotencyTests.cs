using Ago.Chat.Contracts;
using Ago.Chat.Domain;
using Ago.Chat.FakeCrm.Tests;
using Ago.Chat.Webhooks;
using Ago.Platform.Abstractions;
using Ago.Platform.Messaging.RabbitMq;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Ago.Chat.Integration.Tests;

/// <summary>
/// `6-05`'s idempotency Done-when: a redelivered `ConversationAssignedToOperator` (same
/// `EventEnvelope.MessageId`, forced here via a duplicate publish - the backlog's own "a duplicate
/// outbox row" option, the more deterministic of the two named ways to force it) must not produce a
/// second delivery to an endpoint that already succeeded. Runs the real
/// <see cref="ConversationAssignmentWebhookDispatchConsumer"/> against a real RabbitMQ and a real
/// `Ago.Chat.FakeCrm` process - the unique index on `(endpoint_id, message_id)`
/// (`WebhookDeliveryConfiguration`) is what is actually being proven here, not the in-memory fast-path
/// check alone.
/// </summary>
[Collection(WebhookDispatchCollection.Name)]
public sealed class WebhookDispatchIdempotencyTests(WebhookDispatchFixture fixture)
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task ARedeliveredEvent_DoesNotProduceASecondDeliveryToAnAlreadySucceededEndpoint()
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

        var (services, _) = WebhookDispatchTestHarness.BuildConsumerServices(
            fixture,
            WebhookDispatchTestHarness.ResilienceOptions(),
            WebhookDispatchTestHarness.HttpOptions());
        await using var servicesDisposable = services;

        await using var consumerConnection = fixture.CreateRabbitMqConnection();
        var consumer = new ConversationAssignmentWebhookDispatchConsumer(
            new RabbitMqEventConsumer(consumerConnection), services.GetRequiredService<IServiceScopeFactory>(),
            Options.Create(new ConversationAssignmentWebhookDispatchConsumerOptions()),
            NullLogger<ConversationAssignmentWebhookDispatchConsumer>.Instance);

        await consumer.StartAsync(CancellationToken.None);
        try
        {
            await Task.Delay(TimeSpan.FromMilliseconds(500)); // subscription to actually land

            await using var publisherConnection = fixture.CreateRabbitMqConnection();
            var publisher = new RabbitMqEventPublisher(publisherConnection);

            var envelope = BuildEnvelope(siteId, visitorId, operatorId);

            // The redelivery: the exact same envelope (same MessageId) published twice - what
            // `messaging.md`'s at-least-once guarantee promises can happen for real (a broker
            // redelivery after a nack, or - as forced here - a duplicate outbox row from
            // `EfOutboxWriter`'s own at-least-once publish loop).
            await publisher.PublishAsync(envelope, CancellationToken.None);
            await publisher.PublishAsync(envelope, CancellationToken.None);

            var delivered = await OutboxTestHelpers.WaitUntilAsync(
                () =>
                {
                    using var scope = services.CreateScope();
                    var db = scope.ServiceProvider.GetRequiredService<Ago.Chat.Infrastructure.Postgres.Persistence.AgoChatDbContext>();
                    return db.WebhookDeliveries.Count(d => d.EndpointId == endpoint.Id) >= 1;
                },
                TimeSpan.FromSeconds(15));
            Assert.True(delivered, "Timed out waiting for the first delivery to be recorded.");

            // Give the second (duplicate) publish every reasonable chance to also be processed and
            // (wrongly) produce a second row, rather than asserting the count immediately after the
            // first one appears.
            await Task.Delay(TimeSpan.FromSeconds(3));
        }
        finally
        {
            await consumer.StopAsync(CancellationToken.None);
        }

        await using var verify = fixture.CreateDbContext();
        var deliveries = verify.WebhookDeliveries.Where(d => d.EndpointId == endpoint.Id).ToList();
        var delivery = Assert.Single(deliveries);
        Assert.Equal(WebhookDeliveryStatus.Delivered, delivery.Status);
    }

    private static EventEnvelope BuildEnvelope(SiteId siteId, VisitorId visitorId, OperatorId operatorId)
    {
        var messageId = Guid.NewGuid();
        var contract = new ConversationAssignedToOperator(
            ConversationId: Guid.NewGuid(), SiteId: siteId.Value, VisitorId: visitorId.Value, OperatorId: operatorId.Value,
            CorrelationId: Guid.NewGuid(), OccurredAt: Now);

        return new EventEnvelope(
            MessageId: messageId,
            Type: nameof(ConversationAssignedToOperator),
            Version: 1,
            PartitionKey: contract.ConversationId.ToString(),
            OccurredAt: Now,
            CorrelationId: contract.CorrelationId,
            Payload: System.Text.Json.JsonSerializer.Serialize(contract));
    }
}
