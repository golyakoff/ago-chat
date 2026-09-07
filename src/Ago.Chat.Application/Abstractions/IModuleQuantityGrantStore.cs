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
    /// <summary>The currently granted quantity for one site's module, or zero when none has ever been
    /// granted - the identical "missing means nothing, not an error" shape
    /// <c>IRoleAssignmentProjectionStore.GetPermissionsAsync</c> uses on the calendar side for an
    /// analogous absence.</summary>
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
    /// </summary>
    Task GrantAsync(
        SiteId siteId, ModuleKey moduleKey, int quantity, DateTimeOffset now, CancellationToken cancellationToken);
}
