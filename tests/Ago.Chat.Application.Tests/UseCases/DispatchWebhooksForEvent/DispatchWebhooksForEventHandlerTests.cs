using Ago.Chat.Application.Abstractions;
using Ago.Chat.Application.Tests.Fakes;
using Ago.Chat.Application.UseCases.DispatchWebhooksForEvent;
using Ago.Chat.Domain;

namespace Ago.Chat.Application.Tests.UseCases.DispatchWebhooksForEvent;

/// <summary>
/// `6-05`: what belongs at this level - the fan-out and idempotency-skip *decisions*, with
/// <see cref="FakeWebhookDeliveryClient"/> standing in for the entire HTTP/resilience stack. The
/// resilience mechanisms themselves (breaker/bulkhead/timeouts/retry-with-jitter) have no fake
/// standing in for them here on purpose - they are proven for real, against a real
/// <c>Ago.Chat.FakeCrm</c> process, in <c>Ago.Chat.Integration.Tests</c>.
/// </summary>
public class DispatchWebhooksForEventHandlerTests
{
    private static readonly SiteId SiteId = new(Guid.NewGuid());
    private static readonly Guid ConversationId = Guid.NewGuid();
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    private static WebhookEndpoint NewEndpoint(SiteId siteId, bool active = true)
    {
        var endpoint = WebhookEndpoint.Register(
            new WebhookEndpointId(Guid.NewGuid()), siteId, new Uri("https://shop.example.com/hooks"),
            secretCiphertext: System.Text.Encoding.UTF8.GetBytes("whsec_test"), Now);
        if (!active)
        {
            endpoint.Revoke();
        }

        return endpoint;
    }

    private static (
        DispatchWebhooksForEventHandler Handler,
        FakeWebhookEndpointRepository Endpoints,
        FakeWebhookDeliveryRepository Deliveries,
        FakeWebhookDeliveryClient Client) CreateHandler()
    {
        var endpoints = new FakeWebhookEndpointRepository();
        var deliveries = new FakeWebhookDeliveryRepository();
        var client = new FakeWebhookDeliveryClient();
        var handler = new DispatchWebhooksForEventHandler(
            endpoints, deliveries, new FakeWebhookSecretCipher(), client, new FakeIdGenerator(), new FakeClock(Now));
        return (handler, endpoints, deliveries, client);
    }

    [Fact]
    public async Task HandleAsync_OneActiveEndpoint_DeliversAndRecordsIt()
    {
        var (handler, endpoints, deliveries, client) = CreateHandler();
        var endpoint = NewEndpoint(SiteId);
        endpoints.Seed(endpoint);
        client.NextOutcome = new WebhookDeliveryAttemptOutcome(1, WebhookDeliveryStatus.Delivered, 200, "OK");

        var result = await handler.HandleAsync(
            new DispatchWebhooksForConversationEvent(Guid.NewGuid(), SiteId, ConversationId, WebhookEventTypes.ConversationClosed, Now),
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(1, result.Value);
        var saved = Assert.Single(deliveries.Saved);
        Assert.Equal(endpoint.Id, saved.EndpointId);
        Assert.Equal(WebhookDeliveryStatus.Delivered, saved.Status);
        Assert.Single(client.Calls);
    }

    [Fact]
    public async Task HandleAsync_NoActiveEndpoints_NeverCallsTheDeliveryClient()
    {
        var (handler, endpoints, deliveries, client) = CreateHandler();
        endpoints.Seed(NewEndpoint(SiteId, active: false));

        var result = await handler.HandleAsync(
            new DispatchWebhooksForConversationEvent(Guid.NewGuid(), SiteId, ConversationId, WebhookEventTypes.ConversationClosed, Now),
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(0, result.Value);
        Assert.Empty(client.Calls);
        Assert.Empty(deliveries.Saved);
    }

    [Fact]
    public async Task HandleAsync_EndpointForADifferentSite_IsNeverAttempted()
    {
        var (handler, endpoints, _, client) = CreateHandler();
        var otherSite = new SiteId(Guid.NewGuid());
        endpoints.Seed(NewEndpoint(otherSite));

        await handler.HandleAsync(
            new DispatchWebhooksForConversationEvent(Guid.NewGuid(), SiteId, ConversationId, WebhookEventTypes.ConversationClosed, Now),
            CancellationToken.None);

        Assert.Empty(client.Calls);
    }

    [Fact]
    public async Task HandleAsync_MultipleActiveEndpoints_DeliversToEachIndependently()
    {
        var (handler, endpoints, deliveries, client) = CreateHandler();
        endpoints.Seed(NewEndpoint(SiteId));
        endpoints.Seed(NewEndpoint(SiteId));
        endpoints.Seed(NewEndpoint(SiteId));

        var result = await handler.HandleAsync(
            new DispatchWebhooksForConversationEvent(Guid.NewGuid(), SiteId, ConversationId, WebhookEventTypes.ConversationAssigned, Now),
            CancellationToken.None);

        Assert.Equal(3, result.Value);
        Assert.Equal(3, client.Calls.Count);
        Assert.Equal(3, deliveries.Saved.Count);
        // Every endpoint got the identical payload - "one signed payload every endpoint gets"
        // (DispatchWebhooksForEventHandler's own remarks).
        Assert.Single(client.Calls.Select(c => c.PayloadJson).Distinct());
    }

    [Fact]
    public async Task HandleAsync_MessageAlreadyDeliveredToThisEndpoint_SkipsItWithoutCallingTheClientAgain()
    {
        // The idempotency fast-path: a redelivered event for an endpoint the (fake) ledger already
        // has a row for must not re-attempt delivery - IWebhookDeliveryRepository's own remarks
        // ("the fast-path idempotency pre-check... never the sole guarantee" - the sole guarantee is
        // the real unique index, proven against real Postgres in Ago.Chat.Integration.Tests).
        var (handler, endpoints, deliveries, client) = CreateHandler();
        var endpoint = NewEndpoint(SiteId);
        endpoints.Seed(endpoint);
        var messageId = Guid.NewGuid();

        var alreadyDelivered = WebhookDelivery.Record(
            new WebhookDeliveryId(Guid.NewGuid()), endpoint.Id, messageId, WebhookEventTypes.ConversationClosed, "{}",
            attempt: 1, WebhookDeliveryStatus.Delivered, 200, "OK", Now, Now);
        await deliveries.SaveAsync(alreadyDelivered, CancellationToken.None);

        var result = await handler.HandleAsync(
            new DispatchWebhooksForConversationEvent(messageId, SiteId, ConversationId, WebhookEventTypes.ConversationClosed, Now),
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(0, result.Value);
        Assert.Empty(client.Calls);
        Assert.Single(deliveries.Saved); // still just the one seeded above - nothing new written
    }

    [Fact]
    public async Task HandleAsync_ADifferentMessageIdToTheSameEndpoint_IsNotSkipped()
    {
        var (handler, endpoints, deliveries, client) = CreateHandler();
        var endpoint = NewEndpoint(SiteId);
        endpoints.Seed(endpoint);

        var firstDelivered = WebhookDelivery.Record(
            new WebhookDeliveryId(Guid.NewGuid()), endpoint.Id, Guid.NewGuid(), WebhookEventTypes.ConversationClosed, "{}",
            attempt: 1, WebhookDeliveryStatus.Delivered, 200, "OK", Now, Now);
        await deliveries.SaveAsync(firstDelivered, CancellationToken.None);

        var result = await handler.HandleAsync(
            new DispatchWebhooksForConversationEvent(Guid.NewGuid(), SiteId, ConversationId, WebhookEventTypes.ConversationClosed, Now),
            CancellationToken.None);

        Assert.Equal(1, result.Value);
        Assert.Single(client.Calls);
        Assert.Equal(2, deliveries.Saved.Count);
    }

    [Fact]
    public async Task HandleAsync_DeadLetteredOutcome_IsStillRecorded()
    {
        var (handler, endpoints, deliveries, client) = CreateHandler();
        endpoints.Seed(NewEndpoint(SiteId));
        client.NextOutcome = new WebhookDeliveryAttemptOutcome(4, WebhookDeliveryStatus.DeadLettered, 503, "Service Unavailable");

        await handler.HandleAsync(
            new DispatchWebhooksForConversationEvent(Guid.NewGuid(), SiteId, ConversationId, WebhookEventTypes.ConversationClosed, Now),
            CancellationToken.None);

        var saved = Assert.Single(deliveries.Saved);
        Assert.Equal(WebhookDeliveryStatus.DeadLettered, saved.Status);
        Assert.Equal(4, saved.Attempt);
        Assert.Null(saved.DeliveredAt);
    }
}
