using Ago.Chat.Domain;

namespace Ago.Chat.Application.Abstractions;

/// <summary>
/// `22-07`/`adr/0093`: the write-side port for <see cref="ModuleQuantityGrant"/> - "chat grants; the
/// calendar holds and applies; propagation rides the outbox" (this item's own Scope), and this is the
/// grant half. Not a plain CRUD repository: <see cref="GrantAsync"/> is a whole use case in one call,
/// the same shape <c>ISiteRegistrationRepository</c>/<c>IOperatorInviteRedemptionRepository</c>
/// already establish for "a state change and its integration event, committed together" (rule 4) -
/// staging the grant and enqueuing <c>ModuleQuantityGranted</c> are not two things a caller
/// coordinates, they are one write.
/// </summary>
public interface IModuleQuantityGrantStore
{
    /// <summary>The currently <em>effective</em> quantity for one site's module, or zero when none has
    /// ever been granted and no unconditional grant is set - the identical "missing means nothing, not
    /// an error" shape <c>IRoleAssignmentProjectionStore.GetPermissionsAsync</c> uses on the calendar
    /// side for an analogous absence.
    ///
    /// <para><b>`23-86`: effective, not raw.</b> <see cref="ModuleQuantityGrant.EffectiveQuantity"/> -
    /// the last quantity written OR'd with the platform owner's own unconditional-grant flag - never
    /// <see cref="ModuleQuantityGrant.Quantity"/> alone. Every caller of this method already means "is
    /// this entitlement currently on", which is exactly what the OR'd answer is for; nothing in this
    /// codebase has ever wanted the pre-flag number instead.</para></summary>
    Task<int> GetQuantityAsync(SiteId siteId, ModuleKey moduleKey, CancellationToken cancellationToken);

    /// <summary>
    /// `23-66`: every module this site has an explicit grant row for, keyed by <see cref="ModuleKey"/> -
    /// deliberately not <see cref="GetQuantityAsync"/> widened to a site-wide read, because that method's
    /// own "missing means zero" collapses exactly the distinction the platform owner's detail screen
    /// must not lose: a module with <b>no</b> row here has never been granted a quantity at all, while a
    /// module present with value <c>0</c> was granted zero on purpose (this item's own "zero is a
    /// legitimate quota" warning). A module absent from the returned map is the caller's cue to render
    /// "not granted"; a module present at <c>0</c> is the caller's cue to render "granted: 0".
    /// </summary>
    Task<IReadOnlyDictionary<ModuleKey, int>> GetAllForSiteAsync(SiteId siteId, CancellationToken cancellationToken);

    /// <summary>
    /// Sets the granted quantity to exactly <paramref name="quantity"/> and enqueues
    /// <c>ModuleQuantityGranted</c> in the same transaction as the row it describes - rule 4, and the
    /// reason this is not <see cref="GetQuantityAsync"/> followed by a separate save. A snapshot, not a
    /// delta (<see cref="ModuleQuantityGrant"/>'s own remarks): calling this twice with the identical
    /// <paramref name="quantity"/> is a no-op on the stored row and republishes the identical fact,
    /// which is exactly what makes a retried request (or `22-08`'s own reconciliation) safe to repeat.
    ///
    /// <para><b>`23-86`: every caller of this method - <c>SubscriptionRenewalApplier</c>'s own
    /// billing-driven grant/revoke included - stays exactly as it was before this item; nothing here
    /// changed for them.</b> <paramref name="quantity"/> is still written to
    /// <see cref="ModuleQuantityGrant.Quantity"/> unchanged. What changed is what this call
    /// <em>publishes</em>: the <c>ModuleQuantityGranted</c> event now carries
    /// <see cref="ModuleQuantityGrant.EffectiveQuantity"/> (this row's raw <paramref name="quantity"/>
    /// OR'd with whatever <see cref="SetUnconditionalGrantAsync"/> last set), computed inside this one
    /// implementation rather than by every caller re-deriving it - the reason a billing lapse while the
    /// platform owner's flag is set can no longer publish a fact ("revoked") that contradicts what the
    /// flag promises, without a single call site needing to know the flag exists at all.</para>
    /// </summary>
    Task GrantAsync(
        SiteId siteId, ModuleKey moduleKey, int quantity, DateTimeOffset now, CancellationToken cancellationToken);

    /// <summary>
    /// `23-86`: the platform owner's own write - sets or lifts the unconditional-grant flag for one
    /// (site, module), and republishes <c>ModuleQuantityGranted</c> with the freshly recomputed
    /// <see cref="ModuleQuantityGrant.EffectiveQuantity"/> in the same transaction (rule 4), since
    /// flipping the flag alone can change what is effectively granted without
    /// <see cref="GrantAsync"/> ever being called. Creates the row (quantity zero) if none exists yet -
    /// an owner may grant unconditionally before any billing quantity was ever written, which is
    /// exactly case 1 of this item's own "Answered" section (a trial given by hand, no payment yet).
    /// </summary>
    /// <param name="setBy">The platform owner's own Keycloak <c>sub</c> - see
    /// <see cref="ModuleQuantityGrant.UnconditionalGrantSetBy"/>'s own remarks for why this is a raw
    /// string, never <see cref="OperatorId"/>.</param>
    /// <param name="reason">Required, non-blank - <see cref="ModuleQuantityGrant.SetUnconditionalGrant"/>
    /// refuses a blank one.</param>
    Task SetUnconditionalGrantAsync(
        SiteId siteId, ModuleKey moduleKey, bool unconditionallyGranted, string setBy, string reason,
        DateTimeOffset now, CancellationToken cancellationToken);
}
