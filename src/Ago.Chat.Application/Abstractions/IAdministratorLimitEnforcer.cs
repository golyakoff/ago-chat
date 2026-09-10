using Ago.Chat.Domain;

namespace Ago.Chat.Application.Abstractions;

/// <summary>
/// `25-41`: the author's own decision, 2026-09-10, made mechanical - when a site's <see cref="Site.AdminLimit"/>
/// drops (a paid Administrator slot lapsing, a downgrade, or a tier change), the operator(s) above the
/// new ceiling are demoted back to the seeded "Operator" role automatically, deliberately diverging from
/// `23-88`'s own "downgrade destroys nothing" precedent - see <see cref="Site.ActivateSubscription"/>'s
/// own remarks for the full reasoning. Its own port, not folded into <see cref="ISeatChangeApplier"/>/
/// <see cref="ISubscriptionRenewalApplier"/>: every caller of <see cref="Site.ActivateSubscription"/>
/// needs the identical check ("did AdminLimit just drop, and if so demote"), so it belongs once, here,
/// rather than duplicated at each of their own call sites.
///
/// <para><b>Tie-break: most-recently-promoted-to-Administrator first, `adr/0125`'s own precedent.</b>
/// See <c>AdministratorLimitEnforcer</c> (`Ago.Chat.Infrastructure.Postgres`)'s own remarks for exactly
/// how "most recently promoted" is determined - the one real design decision this item's own backlog
/// left open, and the one this port's own implementation answers.</para>
///
/// <para><b>Must run inside the caller's own ambient transaction</b> - the identical contract
/// <see cref="IOperatorRoleRepository.ReplaceRoleAsync"/> and <see cref="IRoleChangeRecordRepository.RecordAsync"/>
/// already state, since a demotion has to commit or roll back atomically with whatever
/// <see cref="Site.ActivateSubscription"/> call caused it - the site's own new, lower ceiling and the
/// operators actually brought back into compliance with it are one fact, not two.</para>
/// </summary>
public interface IAdministratorLimitEnforcer
{
    /// <summary>No-op when <paramref name="newAdminLimit"/> is not below how many non-removed
    /// operators on <paramref name="siteId"/> currently hold the seeded "Admin" role - every caller
    /// passes this unconditionally, after its own call to <see cref="Site.ActivateSubscription"/>,
    /// rather than each pre-checking "did it drop" itself (the identical "let the callee decide there
    /// is nothing to do" shape <see cref="IOperatorRoleRepository.CountNonRemovedHoldersAsync"/>'s own
    /// callers already use for the analogous capacity check).</summary>
    Task DemoteExcessAdministratorsAsync(SiteId siteId, int newAdminLimit, DateTimeOffset now, CancellationToken cancellationToken);
}
