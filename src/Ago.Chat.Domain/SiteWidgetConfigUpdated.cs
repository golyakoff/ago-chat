namespace Ago.Chat.Domain;

/// <summary>
/// `11-01`: the first domain event <see cref="Site"/> ever raises - it has been create-only since
/// `1-04`, so nothing needed this mechanism until <see cref="Site.UpdateWidgetConfig"/> gave it a real
/// update path. Carries <see cref="PublicKey"/> alongside <see cref="SiteId"/> deliberately, the same
/// reason <c>Ago.Chat.Contracts.SiteSettingsChanged</c> (the integration event this maps to,
/// `Ago.Chat.Application.Mapping.SiteWidgetConfigUpdatedMapper`) already does: it is exactly what
/// `SiteCacheInvalidationConsumer` needs to build the cache key it must invalidate
/// (`SiteCacheKeys.ForPublicKey`), and re-deriving it from <see cref="SiteId"/> would cost that
/// consumer a database round trip for a value the publisher already had in hand.
/// </summary>
public sealed record SiteWidgetConfigUpdated(SiteId SiteId, string PublicKey, DateTimeOffset OccurredAt) : IDomainEvent;
