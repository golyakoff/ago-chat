using System.Text.Json;
using Ago.Chat.Application.Abstractions;
using Ago.Chat.Contracts;
using Ago.Chat.Domain;
using Ago.Platform.Kernel;

namespace Ago.Chat.Application.UseCases.DispatchWebhooksForEvent;

/// <summary>
/// `6-05`: the shared fan-out logic behind both new `Ago.Chat.Webhooks` consumers
/// (`ConversationAssignmentWebhookDispatchConsumer`/`ConversationClosedWebhookDispatchConsumer`) -
/// resolve the site's active endpoints, build the one signed payload every endpoint gets, then
/// deliver to each independently ("one endpoint's failure never blocks another's for the same event",
/// backlog's own scope). Every endpoint's delivery attempt runs behind
/// <see cref="IWebhookDeliveryClient"/>, which owns the entire per-endpoint circuit breaker /
/// per-tenant bulkhead / layered-timeout / retry-with-backoff pipeline - this handler stays ignorant
/// of all of it, exactly the same split `SendVisitorMessageHandler` draws from `IMessagePipeline` or
/// `RedisCache`'s own callers draw from its resilience pipeline (rule 2, `clean-architecture.md`: an
/// external resource sits behind a port, never behind Polly/HttpClient types leaking into Application).
///
/// Concurrency, not a sequential loop: <see cref="Task.WhenAll(IEnumerable{Task})"/> over every active
/// endpoint is what actually makes "one endpoint's failure never blocks another's" true for *time*, not
/// only for exceptions - a sequential `foreach` awaiting each delivery in turn would still isolate
/// failures correctly but let one `hangs` endpoint's full timeout-and-retry budget serialize in front
/// of every other endpoint for the same site, which is exactly the bulkhead's own job to prevent and
/// would defeat proving it end to end.
///
/// <see cref="_dbAccess"/> serializes only the two repository calls in <see cref="DeliverToEndpointAsync"/>
/// (<see cref="IWebhookEndpointRepository"/>/<see cref="IWebhookDeliveryRepository"/> are constructed
/// once per `HandleAsync` call and share one unit of work, per this codebase's own "one `DbContext` per
/// scope" convention, `RecordUnreadMessageHandler`'s remarks) - never <see cref="IWebhookDeliveryClient.DeliverAsync"/>,
/// which stays fully concurrent across endpoints. A shared EF Core `DbContext` is not safe for
/// concurrent use from multiple fibers (`ConcurrencyDetector` throws `InvalidOperationException` the
/// moment two calls overlap) - found by running this handler against a site with more than one active
/// endpoint and hitting that exact exception, not assumed from EF Core's own documented rule. Giving
/// every endpoint its own `DbContext` instead (a scope-per-endpoint) was rejected: Application must not
/// depend on `IServiceScopeFactory`/anything DI-container-shaped (clean-architecture.md's dependency
/// rule), and the two calls being serialized here are single-row, sub-millisecond operations - the real
/// per-endpoint cost this backlog cares about isolating (an HTTP round trip that may hang for 30s) never
/// touches this gate at all.
/// </summary>
public sealed class DispatchWebhooksForEventHandler(
    IWebhookEndpointRepository endpoints,
    IWebhookDeliveryRepository deliveries,
    IWebhookSecretCipher cipher,
    IWebhookDeliveryClient client,
    IIdGenerator idGenerator,
    IClock clock)
{
    private readonly SemaphoreSlim _dbAccess = new(1, 1);

    public async Task<Result<int>> HandleAsync(DispatchWebhooksForConversationEvent command, CancellationToken cancellationToken)
    {
        var siteEndpoints = await endpoints.GetAllForSiteAsync(command.SiteId, cancellationToken);
        var active = siteEndpoints.Where(e => e.Active).ToList();
        if (active.Count == 0)
        {
            return Result<int>.Success(0);
        }

        // Built once, sent identically to every endpoint - `event_type`/conversation id/timestamps
        // only, never a message body (backlog's own scope note). Signed per-endpoint by
        // IWebhookDeliveryClient (each endpoint has its own secret), but the bytes being signed are
        // the same for all of them.
        var payloadJson = JsonSerializer.Serialize(
            new WebhookEventPayload(command.EventType, command.ConversationId, command.OccurredAt),
            WireJsonOptions.Options);

        var results = await Task.WhenAll(active.Select(endpoint => DeliverToEndpointAsync(endpoint, command, payloadJson, cancellationToken)));
        return Result<int>.Success(results.Count(delivered => delivered));
    }

    private async Task<bool> DeliverToEndpointAsync(
        WebhookEndpoint endpoint, DispatchWebhooksForConversationEvent command, string payloadJson, CancellationToken cancellationToken)
    {
        // Fast-path idempotency skip (IWebhookDeliveryRepository's own remarks): cheap, and the common
        // case for a broker redelivery of an event whose earlier delivery to this endpoint already
        // committed. Not the correctness guarantee on its own - SaveAsync's unique-index violation
        // below is what still holds if two overlapping attempts both pass this check. Gated by
        // _dbAccess (this class's own remarks) - the only reason this is behind a lock at all is the
        // shared DbContext underneath, not a business rule.
        if (await GatedAsync(() => deliveries.ExistsAsync(endpoint.Id, command.MessageId, cancellationToken)))
        {
            return false;
        }

        var secret = cipher.Decrypt(endpoint.SecretCiphertext);
        // The one call in this method deliberately outside the gate - the HTTP round trip (and every
        // retry/backoff/timeout inside it) is exactly the per-endpoint work this backlog requires to
        // run independently, never serialized behind another endpoint's own delivery.
        var outcome = await client.DeliverAsync(endpoint, command.EventType, payloadJson, secret, cancellationToken);

        var delivery = WebhookDelivery.Record(
            new WebhookDeliveryId(idGenerator.NewId(clock.UtcNow)),
            endpoint.Id,
            command.MessageId,
            command.EventType,
            payloadJson,
            outcome.AttemptCount,
            outcome.Status,
            outcome.ResponseStatus,
            outcome.ResponseSnippet,
            clock.UtcNow,
            outcome.Status == WebhookDeliveryStatus.Delivered ? clock.UtcNow : null);

        return await GatedAsync(() => deliveries.SaveAsync(delivery, cancellationToken));
    }

    private async Task<T> GatedAsync<T>(Func<Task<T>> operation)
    {
        await _dbAccess.WaitAsync();
        try
        {
            return await operation();
        }
        finally
        {
            _dbAccess.Release();
        }
    }
}
