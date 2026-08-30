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
    /// <summary>`14-12`: filtered to <see cref="ChannelIdentity.Active"/> - see the port's own remarks
    /// for why.</summary>
    public Task<ChannelIdentity?> FindAsync(
        SiteId siteId, ChannelKind kind, ExternalChannelAddress address, CancellationToken cancellationToken) =>
        db.ChannelIdentities
            .FirstOrDefaultAsync(
                c => c.SiteId == siteId && c.Kind == kind && c.Address == address && c.Active, cancellationToken);

    /// <summary>`14-02`: ordered by <see cref="ChannelIdentity.LastSeenAt"/> descending - a visitor
    /// touching two channels is a real, if rare, case (`ChannelIdentity`'s own remarks), and "the one
    /// heard from most recently" is the least surprising tie-break for "which channel does an operator's
    /// reply go out on." `14-12`: filtered to <see cref="ChannelIdentity.Active"/> - see the port's own
    /// remarks for why.</summary>
    public Task<ChannelIdentity?> FindMostRecentForVisitorAsync(VisitorId visitorId, CancellationToken cancellationToken) =>
        db.ChannelIdentities
            .Where(c => c.VisitorId == visitorId && c.Active)
            .OrderByDescending(c => c.LastSeenAt)
            .FirstOrDefaultAsync(cancellationToken);

    /// <summary>`14-12`: see the port's own remarks - the console's <c>VisitorPanel</c> listing.</summary>
    public async Task<IReadOnlyList<ChannelIdentity>> ListActiveForVisitorAsync(
        VisitorId visitorId, CancellationToken cancellationToken) =>
        await db.ChannelIdentities
            .Where(c => c.VisitorId == visitorId && c.Active)
            .OrderBy(c => c.FirstSeenAt)
            .ToListAsync(cancellationToken);

    /// <summary>`14-12`: by primary key, not filtered to <see cref="ChannelIdentity.Active"/> - the
    /// port's own remarks explain why an unlink handler needs to see the row regardless of its current
    /// state.</summary>
    public Task<ChannelIdentity?> GetByIdAsync(ChannelIdentityId id, CancellationToken cancellationToken) =>
        db.ChannelIdentities.FirstOrDefaultAsync(c => c.Id == id, cancellationToken);

    public async Task SaveAsync(ChannelIdentity identity, CancellationToken cancellationToken)
    {
        if (db.Entry(identity).State == EntityState.Detached)
        {
            db.ChannelIdentities.Add(identity);
        }

        await db.SaveChangesAsync(cancellationToken);
    }
}
