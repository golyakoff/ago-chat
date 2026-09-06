namespace Ago.Chat.Contracts;

/// <summary>
/// `23-11`/`decisions.md` §5's amendment: the account's contact-visibility rung is one setting on the
/// tenant, not one per product - each product reads this fact and applies it to its own store. This is
/// the contract that carries it across the product boundary, the same role
/// <see cref="RoleAssignmentsChanged"/> already plays for the role catalogue
/// (`22-05`/`adr/0093`): a snapshot of the *current, complete* rung, never a delta, for the identical
/// reason that event states for itself - a consumer that upserts its own projection to whatever this
/// says needs no merge logic and is naturally idempotent under at-least-once redelivery
/// (`messaging.md`).
///
/// <para><b>Consumed by `23-12`'s calendar-side projection, and by nothing else in `ago-chat` today -
/// this same write also raises `SiteSettingsChanged`</b> (`SiteContactVisibilityUpdatedMapper`), which
/// is what evicts this site's own cached `SiteConfigDto` entry through the existing
/// `SiteCacheInvalidationConsumer`. Two integration events from one domain fact, not because this
/// event is unfinished the way `SiteSettingsChanged`'s own "documented, not yet wired" history was -
/// because the two audiences are genuinely different: `SiteSettingsChanged` is chat's own
/// cache-invalidation trigger and has never crossed the product boundary
/// (`Ago.Chat.Contracts.SiteSettingsChanged`'s own remarks); a fact `ago-calendar` must learn belongs
/// in the same minimal, cross-boundary vocabulary <see cref="RoleAssignmentsChanged"/> already
/// established, not folded into a contract whose whole reason to exist is chat-internal.</para>
///
/// <para><see cref="Rung"/> is <c>Ago.Chat.Domain.ContactVisibility</c>'s own member name, serialised
/// as text - `"Visible"` or `"MaskedWithReveal"`, never a third value: rung three does not exist in
/// the enum this serialises (that type's own remarks - this project, `Ago.Chat.Contracts`, never
/// references `Ago.Chat.Domain`, so the reference here is by name, not by `cref`), so there is no
/// delta this contract could ever carry that implies a guarantee this system cannot keep.</para>
/// </summary>
public sealed record ContactVisibilityChanged(
    Guid SiteId, string Rung, Guid CorrelationId, DateTimeOffset OccurredAt);
