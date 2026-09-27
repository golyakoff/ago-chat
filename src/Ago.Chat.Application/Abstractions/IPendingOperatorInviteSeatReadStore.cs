using Ago.Chat.Domain;

namespace Ago.Chat.Application.Abstractions;

/// <summary>
/// `26-241`: how many still-outstanding invites already reserve a seat for one role on a site - the
/// pending half of the create-time seat check <see cref="OperatorInviteRedemptionRepository"/>'s own
/// redeem-time check never needed. Before this item, capacity was enforced only at redemption (against
/// real `operator_roles` holders under a lock), so an admin could send more invites than there were
/// seats and the excess invitees only discovered it when they tried to accept. This port lets a
/// sent-but-unredeemed invite reserve its seat: <see cref="OperatorRoleSeatCapacity.CheckForNewInviteAsync"/>
/// counts current holders <em>plus</em> this figure before allowing one more invite out.
///
/// <para><b>"Outstanding" excludes redeemed, revoked and expired invites</b> - a redeemed invite is now
/// a real `operator_roles` holder (counted the other way), and a revoked or expired one holds nothing, so
/// a stuck slot frees itself the moment an admin revokes the invite (<see cref="OperatorInvite.Revoke"/>)
/// or it lapses (<see cref="OperatorInvite.IsExpired"/>) - no separate release path to build.</para>
///
/// <para>Its own read port rather than a method on <see cref="IOperatorInviteRepository"/> - that port is
/// the write side of the aggregate; this is a hand-shaped count over the write model
/// (`adr/0004`), the same split <see cref="IOperatorInviteListReadStore"/>/<see cref="IOperatorInvitePreviewReadStore"/>
/// already draw. Deliberately <em>not</em> a locked, transactional read: the create-time check is a
/// preventive gate, and the authoritative compare-and-set stays the row-locked redeem-time check
/// (`CLAUDE.md` rule 8) - see <see cref="OperatorRoleSeatCapacity.CheckForNewInviteAsync"/>'s own
/// remarks.</para>
/// </summary>
public interface IPendingOperatorInviteSeatReadStore
{
    /// <summary>The number of not-yet-redeemed, not-revoked, not-expired (as of <paramref name="now"/>)
    /// invites on <paramref name="siteId"/> that grant the role identified by
    /// <paramref name="roleId"/>.</summary>
    Task<int> CountOutstandingForRoleAsync(SiteId siteId, Guid roleId, DateTimeOffset now, CancellationToken cancellationToken);
}
