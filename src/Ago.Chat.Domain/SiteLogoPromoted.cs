namespace Ago.Chat.Domain;

/// <summary>
/// `25-160`: raised by <see cref="Site.PromoteLogo"/> once `Ago.Chat.Worker`'s validating consumer
/// confirms a pending upload and promotes it to its permanent public object key. Maps to the same
/// <c>SiteSettingsChanged</c> integration event every other <see cref="Site"/> settings write converges
/// on, per this backlog item's own Scope ("publishes the existing site-settings-changed event") - even
/// though the one thing that actually makes the very next email warm (the branding Redis cache) is
/// populated by the same validating consumer's own write-through, in the same method call, never by a
/// second consumer reacting to this event. Raised anyway, for the same reason
/// <see cref="Site.UpdateCannedResponses"/>'s own remarks say a write with no real consumer of its event
/// would be worse to leave silent than to say plainly: unlike that method, this one *does* have a real
/// consumer (`SiteCacheInvalidationConsumer`, evicting the ordinary `SiteConfigDto` entries - harmless
/// since neither this write's own fields, only the fact that *something* on this site changed, since
/// eviction is idempotent), so raising it costs nothing and keeps one settings-change signal per
/// aggregate rather than a silent second path.
/// </summary>
public sealed record SiteLogoPromoted(SiteId SiteId, string PublicKey, DateTimeOffset OccurredAt) : IDomainEvent;
