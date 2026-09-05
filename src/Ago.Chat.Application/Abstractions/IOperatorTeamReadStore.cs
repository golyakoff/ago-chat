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
    /// "gone" definition <see cref="IOperatorRepository.CountHeldSeatsAsync"/> and
    /// <c>OperatorInviteRedemptionRepository</c>'s own seat-limit check already use.
    ///
    /// <para><b>Deliberately not filtered by <see cref="OperatorTeamMemberItem.HoldsSeat"/>.</b> A
    /// seat-less operator is still a member of the team the tenant can see and toggle back on - and
    /// this list's own row count is exactly the input <c>OperatorInviteRedemptionRepository</c>'s own
    /// <c>operatorCount &gt;= seatLimit</c> check compares against the seat limit at redemption time
    /// (a count of every non-removed row, not <see cref="IOperatorRepository.CountHeldSeatsAsync"/>'s
    /// narrower "holds a seat right now" count). The console's pre-invite check (this item's own Scope:
    /// "the screen shows what an invite costs against the seat limit before it is sent") needs to
    /// predict that exact refusal, not a different, more optimistic number.</para>
    ///
    /// <para>Ordered by display name, nulls last - a row with no name at all (a minted demo operator,
    /// `adr/0104`'s own remarks on why that shape carries neither) sorts after every named row rather
    /// than scattering alphabetically among them.</para>
    /// </summary>
    Task<IReadOnlyList<OperatorTeamMemberItem>> GetForSiteAsync(SiteId siteId, CancellationToken cancellationToken);
}

/// <summary>
/// One row of <see cref="IOperatorTeamReadStore.GetForSiteAsync"/> - a plain projection of the
/// <c>operators</c> table, not the <see cref="Operator"/> aggregate (the same "read store returns
/// rows, not aggregates" shape <see cref="ConversationSummaryItem"/> already established for the
/// conversation side). <paramref name="DisplayName"/>/<paramref name="Email"/> are both
/// <see langword="null"/> for the one row shape that carries neither - a minted demo tenant's own
/// operator, which is never authenticated through Keycloak and so has no claims to copy
/// (`adr/0104`).
/// </summary>
public sealed record OperatorTeamMemberItem(OperatorId OperatorId, string? DisplayName, string? Email, bool HoldsSeat);
