using Ago.Chat.Application.Abstractions;
using Ago.Chat.Domain;
using Ago.Chat.Infrastructure.Postgres.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Ago.Chat.Infrastructure.Postgres;

/// <summary>`25-181`: <see cref="IOwnerSeatGrantStore"/>'s own implementation - the identical
/// "get-or-create, mutate, save" shape <see cref="ModuleQuantityGrantStore"/> already establishes for
/// its own (site, module) grant, minus the outbox publish: nothing outside this bounded context
/// currently needs to know an owner seat grant changed (this item's own Out of scope: "notifying a
/// tenant" is a separate, undecided feature) - publishing an event with no real consumer would be
/// exactly the "a fake fact through the outbox" <c>Site.UpdateWidgetConfig</c>'s own remarks warn
/// against for an unrelated write.</summary>
public sealed class OwnerSeatGrantStore(AgoChatDbContext db) : IOwnerSeatGrantStore
{
    public async Task<int> GetEffectiveExtraAsync(
        SiteId siteId, OwnerSeatGrantRole role, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var grant = await db.OwnerSeatGrants.AsNoTracking()
            .FirstOrDefaultAsync(g => g.SiteId == siteId && g.Role == role, cancellationToken);
        return grant?.EffectiveQuantity(now) ?? 0;
    }

    public async Task GrantAsync(
        SiteId siteId, OwnerSeatGrantRole role, int quantity, string grantedBy, string reason, DateTimeOffset now,
        DateTimeOffset? expiresAt, CancellationToken cancellationToken)
    {
        var grant = await db.OwnerSeatGrants
            .FirstOrDefaultAsync(g => g.SiteId == siteId && g.Role == role, cancellationToken);

        if (grant is null)
        {
            grant = OwnerSeatGrant.Grant(siteId, role, quantity, grantedBy, reason, now, expiresAt);
            db.OwnerSeatGrants.Add(grant);
        }
        else
        {
            grant.SetGrant(quantity, grantedBy, reason, now, expiresAt);
        }

        await db.SaveChangesAsync(cancellationToken);
    }
}
