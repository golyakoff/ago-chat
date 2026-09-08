namespace Ago.Chat.Application.Abstractions;

/// <summary>
/// `23-70`: the one read the invite's own landing page needs, answered for a caller who by definition
/// holds no `OperatorId`/`SiteId` claim and never will until they redeem - "a stranger opening a link
/// they were sent" (this item's own backlog text). A genuinely different question from
/// <see cref="IOperatorInviteRepository"/> (that port's own remarks: shaped around
/// <c>CreateOperatorInviteHandler</c>'s one write) and from <c>IOperatorInviteRedemptionRepository</c>
/// (a transactional write across three tables) - this is a plain, anonymous, cross-aggregate
/// projection with no invariant to protect, so it is Dapper over the write model rather than aggregate
/// loading, the identical "read store returns rows, not aggregates" split `adr/0004` and
/// <see cref="IOperatorTeamReadStore"/> already draw for a sibling table.
///
/// <para><b>What it deliberately does not answer.</b> No operator list, no seat count, no plan, no
/// role name - "the landing page is reachable by strangers... it must not leak anything about the
/// tenant beyond what a person being invited needs to see" (this item's own trap). The three fields
/// below are exactly the three the backlog text names: "which shop, from whom, and that it
/// expires".</para>
/// </summary>
public interface IOperatorInvitePreviewReadStore
{
    /// <summary>
    /// <see langword="null"/> when no invite matches the presented code's hash - answered the same
    /// whether the code truly never existed or this is simply the wrong value, the identical
    /// info-hiding precedent <see cref="IOperatorInviteRedemptionRepository"/>'s own
    /// <c>NotFound</c> case already establishes for the redemption side of this same code.
    /// </summary>
    Task<OperatorInvitePreviewItem?> GetByCodeHashAsync(byte[] codeHash, CancellationToken cancellationToken);
}

/// <summary>
/// One row - <paramref name="InvitedByDisplayName"/> is <see langword="null"/> for the same reason
/// <see cref="OperatorTeamMemberItem.DisplayName"/> can be: the inviting operator predates `23-02`'s
/// own name/email columns, or is a minted demo tenant's operator with no Keycloak claims to copy
/// (`adr/0104`). <paramref name="IsRedeemed"/>/<paramref name="ExpiresAt"/> are the two facts
/// <c>PreviewOperatorInviteHandler</c> needs to pick a status - deliberately returned as plain data
/// rather than as the three-way status itself, so the "is this expired" clock comparison happens once,
/// in Application, against <c>IClock</c>, not duplicated into this read store's own SQL
/// (`adr/0011`: ordering and time decisions do not belong in Infrastructure).
/// </summary>
public sealed record OperatorInvitePreviewItem(
    string SiteName, string? InvitedByDisplayName, DateTimeOffset ExpiresAt, bool IsRedeemed);
