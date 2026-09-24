using Ago.Chat.Domain;

namespace Ago.Chat.Application.Abstractions;

/// <summary>
/// Resolves whether an operator holds a permission for a site (adr/0016). Visitors never go through
/// this port - a visitor holds a capability (its token proves it owns one conversation), not a role,
/// so a visitor's check is a direct participant comparison in the handler, not a lookup here.
/// </summary>
public interface IPermissionChecker
{
    Task<bool> HasPermissionAsync(
        OperatorId operatorId, SiteId siteId, Permission permission, CancellationToken cancellationToken);

    /// <summary>
    /// `5-08`: every permission value string the operator's roles grant for this site, in one round
    /// trip - the console has no other way to learn "what can I do" to gate its own UI (show the
    /// admin nav item, show the attachment-delete button), and asking <see cref="HasPermissionAsync"/>
    /// once per permission the UI might ever care about does not scale the way a single resolved set
    /// does. Same resolution as <see cref="HasPermissionAsync"/>, just returning the set instead of
    /// testing membership in it - not a new mechanism, the same query with a different projection.
    /// </summary>
    Task<IReadOnlyList<string>> GetPermissionsAsync(
        OperatorId operatorId, SiteId siteId, CancellationToken cancellationToken);

    /// <summary>
    /// `23-26`: the last-manager guard's own compare-and-set read (CLAUDE.md rule 8) -
    /// <c>RemoveOperatorHandler</c>'s only way to ask "how many non-removed operators on this site
    /// currently hold <paramref name="permission"/>" from inside its own transaction, never from a
    /// cache or a read model that might lag. Two concurrent removals of a site's last two managers is
    /// exactly the race this exists to close: a cached or out-of-transaction count would let both
    /// succeed.
    ///
    /// <para>Locks the site's own row (`FOR UPDATE`) before counting - the same single-row mutex
    /// <c>OperatorInviteRedemptionRepository</c>'s own seat-limit check already uses for the identical
    /// reason: operator removal is rare and low-contention (a handful of calls ever per site, not a
    /// message send), so serializing every removal for a site behind one lock is the simplest correct
    /// choice, not a performance concession that would matter on a hot path.</para>
    ///
    /// <para>The caller must already hold an ambient transaction
    /// (<see cref="IUnitOfWork.BeginTransactionAsync"/>) opened on the same scoped connection - the
    /// lock this method takes only serializes concurrent callers for as long as that transaction stays
    /// open, exactly like <c>OperatorInviteRedemptionRepository.LockSiteAndReadSeatLimitAsync</c>'s own
    /// contract.</para>
    /// </summary>
    Task<int> CountNonRemovedHoldersAsync(SiteId siteId, Permission permission, CancellationToken cancellationToken);

    /// <summary>
    /// `26-86`: the `NotifyOperatorDevicesHandler.HandleWaitingAsync` reads this instead of
    /// <see cref="CountNonRemovedHoldersAsync"/> - a new-conversation push has no single assignee to
    /// notify, so it needs the identities to send to, not merely how many there are. Same resolution as
    /// <see cref="CountNonRemovedHoldersAsync"/> (every non-removed operator on the site whose roles
    /// grant <paramref name="permission"/>), restated as a list rather than a count - not a new
    /// mechanism, the identical role-join query with a different projection, the same relationship
    /// <see cref="GetPermissionsAsync"/> already has to <see cref="HasPermissionAsync"/>.
    ///
    /// <para><b>No site row lock, unlike <see cref="CountNonRemovedHoldersAsync"/>.</b> That method locks
    /// because it backs a last-manager compare-and-set a caller is about to act on inside a transaction
    /// (CLAUDE.md rule 8). This is an ordinary fan-out read - "who should this best-effort push go to
    /// right now" - with no write decision riding on the exact answer staying frozen until a later
    /// statement commits; the identical distinction <c>IOperatorRepository.AnyOnlineForSiteAsync</c>'s
    /// own remarks draw between a write-gating question and an operational one.</para>
    /// </summary>
    Task<IReadOnlyList<OperatorId>> ListNonRemovedHolderIdsAsync(SiteId siteId, Permission permission, CancellationToken cancellationToken);
}
