using Ago.Chat.Domain;

namespace Ago.Chat.Application.Abstractions;

/// <summary>
/// `23-72`: the one write this codebase has ever needed against `operator_roles` outside bootstrap
/// registration (<see cref="ISiteRegistrationRepository"/>) and invite redemption
/// (<see cref="IOperatorInviteRedemptionRepository"/>) - replacing an existing colleague's whole role
/// assignment with exactly the one role a change names, the same single-role-per-invite shape
/// redemption already establishes for a freshly created operator. Its own port, not a fourth
/// <see cref="IRoleRepository"/> method: that port answers questions about a site's role catalogue, this
/// is a write against one specific operator's own assignment - a different resource, the same split
/// <see cref="IOperatorTeamReadStore"/>'s own remarks draw against <see cref="IOperatorRepository"/> for
/// an analogous "different question over the same table" case.
///
/// <para><b>`25-170`: also the seat-holding half of that same join table.</b> The core decision this
/// item makes is that "holds a seat" is a fact about one <c>(operator, role)</c> pairing, not about an
/// <see cref="Operator"/> account - so `operator_roles` gains its own `HoldsSeat`/`GrantedAt` columns,
/// and every question and write this port answers about seat capacity is now asked here, parameterized
/// by role name, rather than against <c>Domain.Operator.HoldsSeat</c> (removed by this same item) or
/// duplicated once per role the way <see cref="IOperatorInviteRedemptionRepository"/>'s own seat check
/// and <c>ChangeOperatorRoleHandler</c>'s own hand-written Admin-count check used to be.</para>
/// </summary>
public interface IOperatorRoleRepository
{
    /// <summary>
    /// Every role name <paramref name="operatorId"/> currently holds - `ChangeOperatorRoleHandler`'s own
    /// read for <c>RoleChangeRecordToWrite.PreviousRoleNames</c>, taken before the replacement below so
    /// the audit record states what was actually true a moment before the change, not the post-change
    /// state relabelled. Plural because an operator can hold more than one role at once (the account's
    /// own founder holds both seeded roles from registration).
    /// </summary>
    Task<IReadOnlyList<string>> GetRoleNamesAsync(OperatorId operatorId, CancellationToken cancellationToken);

    /// <summary>
    /// Removes every role <paramref name="operatorId"/> currently holds and assigns exactly
    /// <paramref name="roleId"/> instead. Must run inside the caller's own ambient transaction
    /// (<see cref="IUnitOfWork.BeginTransactionAsync"/>) opened on the same scoped connection - the same
    /// contract <see cref="LockAndGetHeldSeatHolderIdsAsync"/> already states, since the whole point is
    /// committing this replacement atomically with the last-administrator/capacity check that decided
    /// whether to allow it (`ChangeOperatorRoleHandler`'s own remarks).
    ///
    /// <para><b>`25-170`: <paramref name="now"/> is stamped as the new row's own <c>GrantedAt</c></b> -
    /// a role change is one of the two places a role is ever assigned to an operator account (the other
    /// is invite redemption), and both now record when. <b>The new row's own <c>HoldsSeat</c> carries
    /// forward from whatever the operator held before</b> - <see langword="true"/> if <em>any</em> role
    /// this operator held a moment ago (read before the delete below) had <c>HoldsSeat = true</c>, else
    /// <see langword="false"/> - never reset to a fresh default. This is deliberately seat-neutral,
    /// matching this method's own pre-`25-170` contract ("a role change never revokes or grants a
    /// seat") as closely as the schema shift allows: an operator moving between exactly one role and
    /// another keeps whatever seat they already held. The founder's own two-role case (moving from
    /// holding both seeded roles down to one) is the one case this "any" reduction cannot preserve with
    /// perfect fidelity if the two roles ever disagree - an accepted, documented judgement call
    /// (`25-170`'s own report), not an oversight: erring toward "still holds a seat" is the safer
    /// direction, since the alternative (silently locking someone out on a role change) is the worse
    /// failure.</para>
    /// </summary>
    Task ReplaceRoleAsync(OperatorId operatorId, Guid roleId, DateTimeOffset now, CancellationToken cancellationToken);

    /// <summary>
    /// `25-170`: the one-rule <c>Operator.CanSignIn</c>'s own input - does this operator currently hold
    /// <em>any</em> role assignment with <c>HoldsSeat = true</c>, across every role it holds, this site
    /// included and no other (an operator only ever holds roles on its own site). Unlike every other
    /// method here, this is not scoped by role name - it is the one question `OperatorSignInEligibility`
    /// actually needs answered.
    /// </summary>
    Task<bool> HoldsAnySeatAsync(OperatorId operatorId, CancellationToken cancellationToken);

    /// <summary>
    /// Does <paramref name="operatorId"/> currently hold the role named <paramref name="roleName"/> on
    /// <paramref name="siteId"/>, with that specific pairing's own <c>HoldsSeat = true</c> - the routing
    /// question (`AssignConversationHandler`'s own self-claim guard, `TransferConversationHandler`'s own
    /// target-eligibility guard), always asked about the seeded <c>"Operator"</c> role specifically:
    /// "may be routed a conversation" has only ever meant holding an active seat on <em>that</em> role,
    /// never any role a caller happens to hold.
    /// </summary>
    Task<bool> HoldsRoleSeatAsync(OperatorId operatorId, SiteId siteId, string roleName, CancellationToken cancellationToken);

    /// <summary>
    /// Every non-removed operator on <paramref name="siteId"/> who currently holds
    /// <paramref name="roleName"/>'s own seat (<c>HoldsSeat = true</c>) - ordered by <c>GrantedAt</c>
    /// descending, tie-broken by <see cref="OperatorId"/>, the identical "most-recently-granted-first"
    /// order <c>AdministratorLimitEnforcer</c>'s own tie-break established (`adr/0125`'s own precedent)
    /// and this item's own reconciliation procedure now applies to both seeded roles identically. No
    /// lock - a plain display read (`GetBillingStatusHandler`'s own <c>SeatsUsed</c>/<c>AdminsUsed</c>),
    /// never the compare-then-act input a capacity decision depends on (`CLAUDE.md` rule 8) - that
    /// question is <see cref="LockAndGetHeldSeatHolderIdsAsync"/>'s alone.
    /// </summary>
    Task<IReadOnlyList<OperatorId>> GetHeldSeatHolderIdsAsync(SiteId siteId, string roleName, CancellationToken cancellationToken);

    /// <summary>
    /// The identical read <see cref="GetHeldSeatHolderIdsAsync"/> makes, but only after locking
    /// <paramref name="siteId"/>'s own `sites` row (`FOR UPDATE`) on this same connection - the shared
    /// primitive both the unified capacity-check procedure (compares the count against a role's own
    /// `Site` limit before allowing one more) and the reconciliation procedure (compares the count
    /// against the limit and disables the excess) are built from, replacing
    /// `OperatorInviteRedemptionRepository`'s own hand-written seat/admin counts and
    /// `ChangeOperatorRoleHandler`'s own hand-written Admin count alike. The identical single-row mutex
    /// <c>IPermissionChecker.CountNonRemovedHoldersAsync</c> already uses for the analogous
    /// last-administrator check, for the identical reason: appointing, inviting or disabling into a
    /// role is rare and low-contention, so serializing every such write behind one lock is the simplest
    /// correct choice.
    ///
    /// <para>The caller must already hold an ambient transaction (<see cref="IUnitOfWork.BeginTransactionAsync"/>)
    /// opened on the same scoped connection - the identical contract <see cref="ReplaceRoleAsync"/>
    /// already states.</para>
    /// </summary>
    Task<IReadOnlyList<OperatorId>> LockAndGetHeldSeatHolderIdsAsync(SiteId siteId, string roleName, CancellationToken cancellationToken);

    /// <summary>
    /// Flips `HoldsSeat` for exactly one <c>(operatorId, roleName)</c> pairing on <paramref name="siteId"/> -
    /// `ToggleOperatorSeatHandler`'s own write (extended by this item to cover the Admin role the same way
    /// it already covered the Operator role) and the reconciliation procedure's own per-excess-holder
    /// write. A no-op, not an error, when the operator does not hold <paramref name="roleName"/> at all -
    /// the identical "a caller that loaded a row it cannot act on has already made a mistake this
    /// method cannot see" posture <c>Operator.ToggleSeat</c>'s own pre-`25-170` remarks took for a
    /// removed operator.
    /// </summary>
    Task SetHoldsSeatAsync(OperatorId operatorId, SiteId siteId, string roleName, bool holdsSeat, CancellationToken cancellationToken);
}
