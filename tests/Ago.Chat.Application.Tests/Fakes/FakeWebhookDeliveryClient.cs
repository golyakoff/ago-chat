using Ago.Chat.Application.Abstractions;
using Ago.Chat.Domain;

namespace Ago.Chat.Application.Tests.Fakes;

/// <summary>
/// `6-05`: stands in for the entire HTTP/resilience stack behind `IWebhookDeliveryClient` - a
/// handler-level test has no business spinning up a real breaker/bulkhead/HTTP client (that is
/// `HttpWebhookDeliveryClient`'s own, `Ago.Chat.Integration.Tests`' job, against a real
/// `Ago.Chat.FakeCrm` process). Records every call it received so a test can assert
/// `DispatchWebhooksForEventHandler` called (or, for the idempotency-skip path, did not call) this
/// port for a given endpoint.
/// </summary>
public sealed class FakeWebhookDeliveryClient : IWebhookDeliveryClient
{
    public List<(WebhookEndpoint Endpoint, string EventType, string PayloadJson, string SigningSecret)> Calls { get; } = [];

    public WebhookDeliveryAttemptOutcome NextOutcome { get; set; } =
        new(1, WebhookDeliveryStatus.Delivered, 200, "OK");

    public Task<WebhookDeliveryAttemptOutcome> DeliverAsync(
        WebhookEndpoint endpoint, string eventType, string payloadJson, string signingSecret, CancellationToken cancellationToken)
    {
        Calls.Add((endpoint, eventType, payloadJson, signingSecret));
        return Task.FromResult(NextOutcome);
    }
}
