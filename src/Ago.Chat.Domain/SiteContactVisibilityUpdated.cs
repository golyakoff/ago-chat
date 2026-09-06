namespace Ago.Chat.Domain;

/// <summary>
/// `23-11`: raised by <see cref="Site.UpdateContactVisibility"/>. Carries the complete current rung,
/// never a delta - the same choice <c>Ago.Chat.Contracts.RoleAssignmentsChanged</c>'s own remarks
/// record for itself, and for the identical reason: a consumer that upserts its own projection to
/// whatever this says needs no merge logic and is naturally idempotent under at-least-once
/// redelivery, which a "the rung just changed" fact with no value attached would not be.
///
/// <para>Mapped twice, from the one instance this event raises, to two different integration events -
/// <c>SiteContactVisibilityUpdatedMapper</c> (chat's own <c>SiteSettingsChanged</c> cache-invalidation
/// contract, the same one every other <see cref="Site"/> settings write already converges on) and
/// <c>ContactVisibilityChangedMapper</c> (<c>Ago.Chat.Contracts.ContactVisibilityChanged</c>, the one
/// contract that crosses the product boundary, for `23-12`'s calendar-side consumer). One domain fact,
/// two audiences with two different reasons to know it - see <c>UpdateContactVisibilityHandler</c>'s
/// own remarks for why both are enqueued from the same write.</para>
/// </summary>
/// <param name="SiteId">The site whose rung changed.</param>
/// <param name="PublicKey">Carried alongside <paramref name="SiteId"/> for the identical reason
/// <see cref="SiteWidgetConfigUpdated"/>'s own remarks give: it is exactly what
/// <c>SiteCacheInvalidationConsumer</c> needs to build the widget-handshake cache key it must also
/// invalidate (<c>SiteCacheKeys.ForPublicKey</c>), and re-deriving it from <paramref name="SiteId"/>
/// would cost that consumer a database round trip for a value the publisher already had in hand.</param>
/// <param name="Rung">The complete current value - never a delta, see this type's own class
/// remarks.</param>
public sealed record SiteContactVisibilityUpdated(
    SiteId SiteId, string PublicKey, ContactVisibility Rung, DateTimeOffset OccurredAt) : IDomainEvent;
