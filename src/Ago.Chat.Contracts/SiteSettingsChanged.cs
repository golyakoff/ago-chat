namespace Ago.Chat.Contracts;

/// <summary>
/// `caching.md`'s invalidation trigger for the site-config cache entry
/// (`GetSiteConfigByPublicKeyHandler`, `SiteCacheInvalidationConsumer`). <see cref="PublicKey"/> travels
/// alongside <see cref="SiteId"/> deliberately: it is exactly what the one consumer of this event
/// needs to build the cache key it must invalidate, and messaging.md's payload rule allows "what a
/// consumer cannot cheaply look up" - re-deriving it from <see cref="SiteId"/> would cost the
/// consumer a database round trip for a value the publisher already had in hand.
///
/// Not produced by anything yet - no use case changes a site's settings today (site administration
/// is Stage 5). `SiteCacheInvalidationConsumer` is written and tested against this contract now so
/// invalidation already works the day a real producer exists - the same "documented, not yet wired"
/// status realtime.md's Client protocol section gives `clientMessageId`.
/// </summary>
public sealed record SiteSettingsChanged(
    Guid MessageId, DateTimeOffset OccurredAt, Guid SiteId, Guid CorrelationId, string PublicKey);
