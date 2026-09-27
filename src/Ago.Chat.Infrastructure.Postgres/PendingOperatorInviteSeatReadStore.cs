using Ago.Chat.Application.Abstractions;
using Ago.Chat.Domain;
using Ago.Chat.Infrastructure.Postgres.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Ago.Chat.Infrastructure.Postgres;

/// <summary>
/// `26-241`: counts the still-outstanding invites that already reserve a seat for one role on a site -
/// the pending half of the create-time capacity check (<see cref="IPendingOperatorInviteSeatReadStore"/>'s
/// own remarks). EF over the same <see cref="AgoChatDbContext"/> the invite aggregate is written through,
/// not a Dapper read store like <see cref="OperatorInviteListReadStore"/>: this counts across the
/// <c>operator_invite_roles</c> child collection the aggregate itself owns, so the EF model already
/// describes the join, and the query is a single indexed <c>COUNT</c> either way.
///
/// <para>"Outstanding" is the same predicate <see cref="PendingOperatorInviteByEmailReadStore"/> already
/// uses for its own steer-away read - unredeemed (<c>redeemed_at IS NULL</c>), unrevoked
/// (<c>revoked_at IS NULL</c>) and unexpired (<c>expires_at &gt; now</c>) - restated here scoped to a
/// role rather than an email, so a revoked or expired invite frees its reserved slot with no separate
/// release path.</para>
/// </summary>
public sealed class PendingOperatorInviteSeatReadStore(AgoChatDbContext db) : IPendingOperatorInviteSeatReadStore
{
    public Task<int> CountOutstandingForRoleAsync(
        SiteId siteId, Guid roleId, DateTimeOffset now, CancellationToken cancellationToken) =>
        db.OperatorInvites.AsNoTracking()
            .Where(invite =>
                invite.SiteId == siteId
                && invite.RedeemedAt == null
                && invite.RevokedAt == null
                && invite.ExpiresAt > now
                && invite.Roles.Any(role => role.RoleId == roleId))
            .CountAsync(cancellationToken);
}
