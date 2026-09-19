using Ago.Chat.Domain;

namespace Ago.Chat.Application.Abstractions;

/// <summary>
/// `23-22`: the team screen's own read - "a tenant... sees every operator by name, and sees which
/// hold seats" (this item's own Done-when). A genuinely different question from every existing
/// <see cref="IOperatorRepository"/> method - that port's own remarks are explicit that it is "not a
/// general operator CRUD port, grow this only when a second real caller needs a different question
/// answered" - so this is its own port rather than a fourth method bolted on. It is Dapper over the
/// write model, never aggregate loading, the same split <c>adr/0004</c> and
/// <see cref="IOperatorAnalyticsReadStore"/> already draw for the identical table: a display listing
/// has no invariant to protect and no reason to pay for EF's change tracking.
/// </summary>
public interface IOperatorTeamReadStore
{
    /// <summary>
    /// Every operator still active on <paramref name="siteId"/> - <c>removed_at IS NULL</c>, the same
    /// "gone" definition <c>OperatorInviteRedemptionRepository</c>'s own seat-limit check already uses.
    ///
    /// <para><b>Deliberately not filtered by seat-holding.</b> A seat-less operator (on either role) is
    /// still a member of the team the tenant can see and toggle back on.</para>
    ///
    /// <para>Ordered by display name, nulls last - a row with no name at all (a minted demo operator,
    /// `adr/0104`'s own remarks on why that shape carries neither) sorts after every named row rather
    /// than scattering alphabetically among them.</para>
    /// </summary>
    Task<IReadOnlyList<OperatorTeamMemberItem>> GetForSiteAsync(SiteId siteId, CancellationToken cancellationToken);
}

/// <summary>
/// `25-170`: one role this operator holds, and whether that specific pairing currently holds a seat -
/// replacing the pre-`25-170` flat <c>HoldsSeat: bool</c> + <c>RoleNames: IReadOnlyList&lt;string&gt;</c>
/// pair on <see cref="OperatorTeamMemberItem"/>, since "holds a seat" is now a fact about one
/// <c>(operator, role)</c> pairing, not the operator account as a whole - an operator holding both
/// seeded roles (the account's own founder) can hold one role's seat and not the other's, which the old
/// single flat boolean had no way to express at all.
/// </summary>
public sealed record OperatorRoleSeatAssignment(string RoleName, bool HoldsSeat);

/// <summary>
/// One row of <see cref="IOperatorTeamReadStore.GetForSiteAsync"/> - a plain projection of the
/// <c>operators</c> table (joined with <c>operator_roles</c>/<c>roles</c> for <see cref="Roles"/>),
/// not the <see cref="Operator"/> aggregate (the same "read store returns rows, not aggregates" shape
/// <see cref="ConversationSummaryItem"/> already established for the conversation side).
/// <paramref name="DisplayName"/>/<paramref name="Email"/> are both <see langword="null"/> for the one
/// row shape that carries neither - a minted demo tenant's own operator, which is never authenticated
/// through Keycloak and so has no claims to copy (`adr/0104`).
///
/// <para><paramref name="Roles"/>: every role this operator currently holds, each with its own seat
/// status - plural because the account's own founder holds both seeded roles from registration
/// (<c>SiteRegistrationRepository</c>'s own remarks), not a single "the" role. The team screen needs
/// this to show who already administers before offering to change anyone's role, and (`25-170`) to
/// render a per-role seat toggle for whichever roles an operator actually holds.</para>
/// </summary>
public sealed record OperatorTeamMemberItem(
    OperatorId OperatorId, string? DisplayName, string? Email, IReadOnlyList<OperatorRoleSeatAssignment> Roles)
{
    /// <summary>Trailing default, matching this codebase's own precedent for a column/field added to a
    /// type with many existing call sites (<see cref="Domain.Site.Name"/>'s own remarks) - every test
    /// that seeds a team row for a question unrelated to roles keeps compiling with no role assignments
    /// at all, which is also an honest empty answer, not a guess.</summary>
    public OperatorTeamMemberItem(OperatorId OperatorId, string? DisplayName, string? Email)
        : this(OperatorId, DisplayName, Email, [])
    {
    }
}
