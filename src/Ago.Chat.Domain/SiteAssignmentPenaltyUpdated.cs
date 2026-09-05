namespace Ago.Chat.Domain;

/// <summary>
/// `23-05`: `Site`'s sixth domain event, raised by <see cref="Site.UpdateAssignmentPenalty"/>. Same
/// shape as <see cref="SiteOfflineAutoReplyUpdated"/>/<see cref="SiteWidgetConfigUpdated"/> and for the
/// identical reason: it maps to the one <c>SiteSettingsChanged</c> integration event every other
/// <c>Site</c> write path converges on, so <c>SiteCacheInvalidationConsumer</c> evicts this site's
/// cached <c>SiteConfigDto</c> under both of its keys the moment the penalty changes.
///
/// <para><b>What that eviction actually buys, and what it does not.</b> The cached config a visitor's
/// per-message hot path reads never carries this value in the first place - <c>SiteConfigDto</c> has
/// no <c>AssignmentPenaltySeconds</c> field, deliberately, because `23-05`'s own scope is explicit that
/// the claimer "reads it inside its own transaction, never from the cache" (`CLAUDE.md` rule 8: never
/// cache what a write decision depends on). This event still exists, and still maps to
/// <c>SiteSettingsChanged</c>, purely to keep every <c>Site</c> write path uniform - a future admin
/// screen reading the console's own cached view of "this site's settings" should not have to special-
/// case the one field that happens to be read differently by its one real consumer.</para>
/// </summary>
public sealed record SiteAssignmentPenaltyUpdated(SiteId SiteId, string PublicKey, DateTimeOffset OccurredAt) : IDomainEvent;
