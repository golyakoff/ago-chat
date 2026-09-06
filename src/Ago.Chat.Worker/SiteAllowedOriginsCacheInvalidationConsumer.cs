using System.Text.Json;
using Ago.Chat.Application.Caching;
using Ago.Chat.Contracts;
using Ago.Platform.Abstractions;
using Ago.Platform.Caching.Redis;
using Microsoft.Extensions.Options;

namespace Ago.Chat.Worker;

/// <summary>
/// `23-48`: the CORS-layer counterpart to <see cref="SiteCacheInvalidationConsumer"/> - maps
/// <see cref="SiteAllowedOriginsChanged"/> (which origins changed) to a <c>CacheInvalidated</c>
/// broadcast for each one's own key (<see cref="CorsOriginCacheKeys.ForOrigin"/>). Its own consumer,
/// not a second subscription folded into <see cref="SiteCacheInvalidationConsumer"/>: that class
/// subscribes to exactly one event type (`SubscribeAsync(nameof(SiteSettingsChanged), ...)`), the same
/// one-<see cref="BackgroundService"/>-one-event-type shape every consumer in this project (`16 of
/// them, `Ago.Chat.Worker`) already follows - two unrelated event types sharing one class would mean
/// one's retry policy, one's logging, and one's failure mode leaking into the other's.
///
/// <para><b>Evicts the union of <see cref="SiteAllowedOriginsChanged.PreviousOrigins"/> and
/// <see cref="SiteAllowedOriginsChanged.AllowedOrigins"/></b> - not just the new list. A removed
/// origin's positive cache entry (`CheckCorsOriginHandler`'s own 5-minute TTL) would otherwise keep
/// answering "allowed" for up to five minutes after this tenant stopped permitting it; an added
/// origin may already carry a negative entry from an earlier, now-stale denied check
/// (`CheckCorsOriginHandler`'s own 30-second negative TTL) that this write should not have to wait
/// out. Re-broadcasting an invalidation for an origin nothing actually cached is free
/// (`SiteCacheInvalidationConsumer`'s own remarks: "re-broadcasting is idempotent and cheap,
/// `adr/0020`") - this consumer does not need to know which of the two lists a given origin came from
/// before evicting it.</para>
///
/// <para>No DI scope needed per message, the identical reasoning
/// <see cref="SiteCacheInvalidationConsumer"/>'s own remarks give: <see cref="CacheInvalidationPublisher"/>
/// depends only on singletons. <c>Competing</c>, matching every other cache-invalidation publisher in
/// this codebase - exactly one <c>Worker</c> replica needs to trigger the broadcast per settings
/// change.</para>
/// </summary>
public sealed class SiteAllowedOriginsCacheInvalidationConsumer(
    IEventConsumer consumer,
    CacheInvalidationPublisher publisher,
    IOptions<SiteAllowedOriginsCacheInvalidationConsumerOptions> options,
    ILogger<SiteAllowedOriginsCacheInvalidationConsumer> logger) : BackgroundService
{
    // `15-17`: `internal`, not `private` - see `ConnectionFanoutConsumer.ConsumerName`'s own remarks
    // for why an integration test needs this exact value rather than a retyped copy of it.
    internal const string ConsumerName = "site-allowed-origins-cache-invalidation";

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var retryPolicy = new RetryPolicy(
            options.Value.MaxAttempts, options.Value.InitialBackoff, $"{ConsumerName}.dlq");

        return consumer.SubscribeAsync(
            nameof(SiteAllowedOriginsChanged), SubscriptionMode.Competing, ConsumerName, retryPolicy, HandleAsync,
            stoppingToken);
    }

    private async Task HandleAsync(EventEnvelope envelope, IMessageContext context, CancellationToken cancellationToken)
    {
        try
        {
            var changed = JsonSerializer.Deserialize<SiteAllowedOriginsChanged>(envelope.Payload)
                ?? throw new InvalidOperationException(
                    $"Could not deserialize {nameof(SiteAllowedOriginsChanged)} payload for {envelope.MessageId}.");

            var staleOrigins = changed.PreviousOrigins.Concat(changed.AllowedOrigins).Distinct(StringComparer.Ordinal);
            foreach (var origin in staleOrigins)
            {
                await publisher.PublishAsync(CorsOriginCacheKeys.ForOrigin(origin), envelope.CorrelationId, cancellationToken);
            }

            await context.AckAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Failed to publish a CORS-origin cache invalidation for {MessageId}.", envelope.MessageId);
            throw; // safe to retry freely - re-broadcasting an invalidation is exactly as harmless as the first one
        }
    }
}
