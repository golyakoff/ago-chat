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

    public async Task SaveAsync(ChannelIdentity identity, CancellationToken cancellationToken)
    {
        if (db.Entry(identity).State == EntityState.Detached)
        {
            db.ChannelIdentities.Add(identity);
        }

        await db.SaveChangesAsync(cancellationToken);
    }
}
