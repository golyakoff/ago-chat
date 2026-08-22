using System.Text.Json;
using Ago.Chat.Application.Caching;
using Ago.Chat.Contracts;
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
    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var retryPolicy = new RetryPolicy(
            options.Value.MaxAttempts, options.Value.InitialBackoff, "site-cache-invalidation.dlq");

        return consumer.SubscribeAsync(
            nameof(SiteSettingsChanged), SubscriptionMode.Competing, retryPolicy, HandleAsync, stoppingToken);
    }

    private async Task HandleAsync(EventEnvelope envelope, IMessageContext context, CancellationToken cancellationToken)
    {
        try
        {
            var changed = JsonSerializer.Deserialize<SiteSettingsChanged>(envelope.Payload)
                ?? throw new InvalidOperationException(
                    $"Could not deserialize {nameof(SiteSettingsChanged)} payload for {envelope.MessageId}.");

            await publisher.PublishAsync(SiteCacheKeys.ForPublicKey(changed.PublicKey), envelope.CorrelationId, cancellationToken);
            await context.AckAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Failed to publish a cache invalidation for {MessageId}.", envelope.MessageId);
            throw; // safe to retry freely - re-broadcasting an invalidation is exactly as harmless as the first one
        }
    }
}
