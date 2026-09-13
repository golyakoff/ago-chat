namespace Ago.Chat.Domain;

/// <summary>
/// `22-08`: raised by <see cref="Site.Suspend"/> and <see cref="Site.LiftSuspension"/> alike - one
/// domain event for both directions, the same "the write is a scalar, not a state machine" reading
/// those two methods' own remarks give for why this aggregate needs no separate guard between them.
///
/// <para><b>A snapshot of the complete current value, never a delta</b> - the same choice
/// <see cref="SiteContactVisibilityUpdated"/> and <c>Ago.Chat.Contracts.RoleAssignmentsChanged</c>
/// already make, for the identical reason stated there: a consumer that upserts its own projection to
/// whatever this says needs no merge logic and is naturally idempotent under at-least-once
/// redelivery - which matters doubly here, since this fact's own cross-boundary mapping
/// (<c>TenantSuspensionChangedMapper</c>) is also re-published on a fixed cadence by
/// <c>Ago.Chat.Worker.SuspensionLeaseRenewalJob</c> for as long as a site stays suspended
/// (`adr/0149` rule 1's own renewal-at-half-the-lease-length), so the identical value is expected to
/// arrive more than once by design, not merely under redelivery.</para>
///
/// <para><b>Mapped once, not twice.</b> Unlike <see cref="SiteContactVisibilityUpdated"/>, this event
/// has no chat-internal cache-invalidation audience - nothing in <c>Ago.Chat.*</c> caches a site's own
/// suspension state (<see cref="Site.SuspendedUntil"/>'s own remarks: every chat-side reader goes
/// through <c>Application.Abstractions.ISiteSuspensionReadStore</c>'s live read, never the cached
/// <c>SiteConfigDto</c>), so there is nothing here for a second, chat-only integration event to
/// invalidate. <c>TenantSuspensionChangedMapper</c> is this event's only mapping, the one contract
/// that crosses the product boundary.</para>
/// </summary>
/// <param name="SiteId">The site whose suspension state changed.</param>
/// <param name="SuspendedUntil">The complete current value - <see langword="null"/> means "not
/// suspended" (a lift, or an aggregate that was never suspended), a real instant means "suspended
/// until this instant", never a delta.</param>
public sealed record SiteSuspensionChanged(SiteId SiteId, DateTimeOffset? SuspendedUntil, DateTimeOffset OccurredAt)
    : IDomainEvent;
