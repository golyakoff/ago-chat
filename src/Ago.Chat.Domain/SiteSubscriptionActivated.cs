namespace Ago.Chat.Domain;

/// <summary>
/// `13-02`: `Site`'s fourth domain event, raised by <see cref="Site.ActivateSubscription"/> - the same
/// convergence-onto-`SiteSettingsChanged` shape <see cref="SiteWidgetConfigUpdated"/>/
/// <see cref="SiteOfflineAutoReplyUpdated"/>/<see cref="SiteLocaleUpdated"/> already established, applied
/// to a fourth kind of setting change (this one driven by a payment, not an operator's own console
/// edit). <see cref="Tier"/>/<see cref="SeatLimit"/> ride along on the event itself, not just `SiteId"/> -
/// nothing downstream needs them today (`SiteCacheInvalidationConsumer` only ever evicts a cache key, it
/// never reads a domain event's payload), but recording the values a write actually applied, rather than
/// only the fact that *something* changed, is what makes this event legible on its own if a future reader
/// (an audit trail, a support tool) ever needs "what did this payment change" without joining back to
/// `billing_subscriptions`.
/// </summary>
public sealed record SiteSubscriptionActivated(
    SiteId SiteId, string PublicKey, string Tier, int SeatLimit, DateTimeOffset OccurredAt) : IDomainEvent;
