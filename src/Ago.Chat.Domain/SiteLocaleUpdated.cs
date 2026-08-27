namespace Ago.Chat.Domain;

/// <summary>
/// `11-10`: <see cref="Site"/>'s third domain event, raised by <see cref="Site.UpdateLocale"/> - a
/// separate event from <see cref="SiteWidgetConfigUpdated"/> rather than folding
/// <see cref="Locale"/> into <see cref="WidgetConfig"/> itself, for the identical reason
/// <see cref="SiteOfflineAutoReplyUpdated"/>'s own remarks give: a widget's display language is not
/// its appearance, and a future consumer that wants only appearance changes must be able to tell them
/// apart. They converge at the integration-event boundary, not before it - both this event and
/// <see cref="SiteWidgetConfigUpdated"/> map to the one <c>SiteSettingsChanged</c> contract
/// (<see cref="SiteOfflineAutoReplyUpdated"/> already established the same convergence for a third
/// setting), because "this site's settings changed, drop its cache entries" is one fact regardless of
/// which setting changed.
///
/// Carries <see cref="PublicKey"/> alongside <see cref="SiteId"/> for the same reason both siblings
/// do: it is exactly what <c>SiteCacheInvalidationConsumer</c> needs to build the cache key it must
/// invalidate, and re-deriving it from <see cref="SiteId"/> would cost that consumer a database round
/// trip for a value the publisher already had in hand.
/// </summary>
public sealed record SiteLocaleUpdated(SiteId SiteId, string PublicKey, DateTimeOffset OccurredAt) : IDomainEvent;
