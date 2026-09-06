namespace Ago.Chat.Domain;

/// <summary>
/// `23-48`: raised by <see cref="Site.UpdateAllowedOrigins"/> - the platform owner's own write, the
/// first to touch <see cref="Site.AllowedOrigins"/> since <see cref="Site"/> was first constructed
/// (`10-02`'s registration handler is the only other writer, and it only ever sets the list once, at
/// creation).
///
/// <para><b>Carries both the previous and the new list, not a delta.</b> Every other <see cref="Site"/>
/// write path since `11-01` carries the complete current value rather than "what changed" - see
/// <see cref="SiteContactVisibilityUpdated"/>'s own remarks for why that shape is what keeps a
/// consumer naturally idempotent under at-least-once redelivery. This event needs more than the
/// current value alone: the CORS-layer cache is keyed by *origin string*
/// (`Ago.Chat.Application.Caching.CorsOriginCacheKeys.ForOrigin`), not by site, so evicting it
/// correctly means evicting every origin this write touched - the ones just removed (their positive
/// "allowed" entry is now a lie) and the ones just added (a prior denied check may have cached them
/// negatively). Only <see cref="PreviousOrigins"/> carries what the removed ones were; the current
/// value alone cannot reconstruct it.</para>
///
/// <para>Mapped twice, from the one instance this event raises, to two different integration events -
/// the same "one domain fact, two audiences with two different reasons to know it" shape
/// <see cref="SiteContactVisibilityUpdated"/> already established. <c>SiteAllowedOriginsUpdatedMapper</c>
/// produces the same <c>Ago.Chat.Contracts.SiteSettingsChanged</c> every other <see cref="Site"/>
/// settings write converges on, for the existing <c>SiteCacheInvalidationConsumer</c> to evict
/// <c>SiteCacheKeys.ForPublicKey</c>/<c>ForSiteId</c> (the site-config cache-aside pair every widget
/// handshake and hub connection reads). <c>SiteAllowedOriginsChangedMapper</c> produces the new
/// <c>Ago.Chat.Contracts.SiteAllowedOriginsChanged</c>, for the new
/// <c>SiteAllowedOriginsCacheInvalidationConsumer</c> to evict the CORS-layer entries this event's own
/// remarks describe above - a cache shape the existing consumer has never touched.</para>
/// </summary>
/// <param name="SiteId">The tenant whose allowed origins changed.</param>
/// <param name="PublicKey">Carried alongside <paramref name="SiteId"/> for the identical reason every
/// other <see cref="Site"/> settings event does: it is exactly what
/// <c>SiteCacheInvalidationConsumer</c> needs to build the widget-handshake cache key
/// (<c>SiteCacheKeys.ForPublicKey</c>), and re-deriving it from <paramref name="SiteId"/> would cost
/// that consumer a database round trip for a value the publisher already had in hand.</param>
/// <param name="PreviousOrigins">The complete list before this write - see this type's own class
/// remarks for why the CORS-cache invalidation needs it.</param>
/// <param name="AllowedOrigins">The complete list after this write - the value every future reader
/// (the widget handshake, the layer-2 hub check, the CORS preflight) now sees.</param>
public sealed record SiteAllowedOriginsUpdated(
    SiteId SiteId,
    string PublicKey,
    IReadOnlyList<string> PreviousOrigins,
    IReadOnlyList<string> AllowedOrigins,
    DateTimeOffset OccurredAt) : IDomainEvent;
