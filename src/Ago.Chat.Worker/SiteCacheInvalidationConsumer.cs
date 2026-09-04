using System.Text.Json;
using Ago.Chat.Application.Caching;
using Ago.Chat.Contracts;
using Ago.Chat.Domain;
using Ago.Platform.Abstractions;
using Ago.Platform.Caching.Redis;
using Microsoft.Extensions.Options;

namespace Ago.Chat.Worker;

/// <summary>
/// `3-04`: the product-specific half of `caching.md`'s event-driven invalidation - maps
/// `SiteSettingsChanged` (which site) to a `CacheInvalidated` broadcast for that site's cache key
/// (`SiteCacheKeys`). The generic half, publishing and broadcasting the invalidation itself, is
/// `Ago.Platform.Caching.Redis.CacheInvalidationPublisher`/`CacheInvalidationConsumer` - the same
/// product/platform split `ConnectionFanoutConsumer`/`Ago.Platform.Realtime.NodeFanoutPublisher`
/// already draw for the fan-out path.
///
/// No DI scope needed per message, unlike `UnreadCounterConsumer`/`ConnectionFanoutConsumer`:
/// `CacheInvalidationPublisher` depends only on `IEventPublisher`/`IClock`, both singletons, so this
/// class takes it directly rather than an `IServiceScopeFactory`. `Competing`, matching those two -
/// exactly one `Worker` replica needs to trigger the broadcast per settings change.
/// </summary>
public sealed class SiteCacheInvalidationConsumer(
    IEventConsumer consumer,
    CacheInvalidationPublisher publisher,
    IOptions<SiteCacheInvalidationConsumerOptions> options,
    ILogger<SiteCacheInvalidationConsumer> logger) : BackgroundService
{
    // `5-11`: this consumer's own stable identity - see `ConnectionFanoutConsumer`'s own remarks.
    //
    // `15-17`: `internal`, not `private` - see `ConnectionFanoutConsumer.ConsumerName`'s own remarks
    // for why a test needs this exact value rather than a retyped copy of it.
    internal const string ConsumerName = "site-cache-invalidation";

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var retryPolicy = new RetryPolicy(
            options.Value.MaxAttempts, options.Value.InitialBackoff, $"{ConsumerName}.dlq");

        return consumer.SubscribeAsync(
            nameof(SiteSettingsChanged), SubscriptionMode.Competing, ConsumerName, retryPolicy, HandleAsync, stoppingToken);
    }

    private async Task HandleAsync(EventEnvelope envelope, IMessageContext context, CancellationToken cancellationToken)
    {
        try
        {
            var changed = JsonSerializer.Deserialize<SiteSettingsChanged>(envelope.Payload)
                ?? throw new InvalidOperationException(
                    $"Could not deserialize {nameof(SiteSettingsChanged)} payload for {envelope.MessageId}.");

            // `14-04`: **both** keys, not just the public-key one. `SiteCacheKeys` has mapped this one
            // row under two keys since `5-01` (`ForPublicKey` for the widget handshake, `ForSiteId` for
            // anything holding a JWT's `site_id` claim), and this consumer only ever evicted the first
            // of them - so a settings write left the id-keyed copy standing until its own five-minute
            // TTL expired. Invisible until `14-04`, because until now nothing read the id-keyed entry
            // for a value an operator had just changed and expected to take effect; `caching.md`'s
            // claim that a config write is "evicted on every node well before the TTL would otherwise
            // expire it" was only half true. Two publishes rather than one key that covers both: the
            // platform's invalidation contract is one key per broadcast, and re-broadcasting is
            // idempotent and cheap (`adr/0020`).
            await publisher.PublishAsync(SiteCacheKeys.ForPublicKey(changed.PublicKey), envelope.CorrelationId, cancellationToken);
            await publisher.PublishAsync(SiteCacheKeys.ForSiteId(new SiteId(changed.SiteId)), envelope.CorrelationId, cancellationToken);
            await context.AckAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Failed to publish a cache invalidation for {MessageId}.", envelope.MessageId);
            throw; // safe to retry freely - re-broadcasting an invalidation is exactly as harmless as the first one
        }
    }
}
