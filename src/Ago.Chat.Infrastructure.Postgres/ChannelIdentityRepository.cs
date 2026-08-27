using Ago.Chat.Application.Abstractions;
using Ago.Chat.Domain;
using Ago.Chat.Infrastructure.Postgres.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Ago.Chat.Infrastructure.Postgres;

/// <summary>`14-01`: the EF adapter for <see cref="IChannelIdentityRepository"/> - the same
/// detached-means-insert shape <see cref="VisitorRepository"/> established, because a resolve-or-create
/// use case is the only caller of either.</summary>
public sealed class ChannelIdentityRepository(AgoChatDbContext db) : IChannelIdentityRepository
{
    public Task<ChannelIdentity?> FindAsync(
        SiteId siteId, ChannelKind kind, ExternalChannelAddress address, CancellationToken cancellationToken) =>
        db.ChannelIdentities
            .FirstOrDefaultAsync(
                c => c.SiteId == siteId && c.Kind == kind && c.Address == address, cancellationToken);

    /// <summary>`14-02`: ordered by <see cref="ChannelIdentity.LastSeenAt"/> descending - a visitor
    /// touching two channels is a real, if rare, case (`ChannelIdentity`'s own remarks), and "the one
    /// heard from most recently" is the least surprising tie-break for "which channel does an operator's
    /// reply go out on."</summary>
    public Task<ChannelIdentity?> FindMostRecentForVisitorAsync(VisitorId visitorId, CancellationToken cancellationToken) =>
        db.ChannelIdentities
            .Where(c => c.VisitorId == visitorId)
            .OrderByDescending(c => c.LastSeenAt)
            .FirstOrDefaultAsync(cancellationToken);

    public async Task SaveAsync(ChannelIdentity identity, CancellationToken cancellationToken)
    {
        if (db.Entry(identity).State == EntityState.Detached)
        {
            db.ChannelIdentities.Add(identity);
        }

        await db.SaveChangesAsync(cancellationToken);
    }
}
