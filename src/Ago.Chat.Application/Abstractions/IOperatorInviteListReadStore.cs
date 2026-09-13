using Ago.Chat.Domain;

namespace Ago.Chat.Application.Abstractions;

/// <summary>
/// `25-73`: the query behind the console's new invite-list screen - "email / date sent / status /
/// expiry / revoke", shown under the operator table only when at least one invite exists for the site
/// (this item's own point 7). Hand-written SQL over the write model, never through the
/// <see cref="OperatorInvite"/> aggregate, the same `adr/0004` split
/// <see cref="IOperatorInvitePreviewReadStore"/> already draws for the identical reason - this is its
/// own port rather than a fourth thing <see cref="IOperatorInviteRepository"/> or
/// <see cref="IOperatorInviteRedemptionRepository"/> answer.
/// </summary>
public interface IOperatorInviteListReadStore
{
    Task<IReadOnlyList<OperatorInviteListItem>> ListForSiteAsync(SiteId siteId, CancellationToken cancellationToken);
}

/// <summary>Every raw fact <c>ListOperatorInvitesHandler</c> needs to compute this row's own status -
/// deliberately the raw <see cref="RedeemedAt"/>/<see cref="RevokedAt"/>/<see cref="ExpiresAt"/> instants
/// and <see cref="SendFailureCode"/>, not a pre-computed status string. `adr/0011`: ordering and time
/// decisions live in Application, not Infrastructure - the same split <see cref="OperatorInvitePreviewItem"/>
/// already draws, so "is this expired" is decided once, against <c>IClock</c>, in the handler, never in
/// this read store's own SQL.</summary>
public sealed record OperatorInviteListItem(
    OperatorInviteId Id,
    string Email,
    DateTimeOffset CreatedAt,
    DateTimeOffset ExpiresAt,
    DateTimeOffset? RedeemedAt,
    DateTimeOffset? RevokedAt,
    string? SendFailureCode);
