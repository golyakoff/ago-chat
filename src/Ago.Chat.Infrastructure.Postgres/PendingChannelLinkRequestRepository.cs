using Ago.Chat.Application.Abstractions;
using Ago.Chat.Domain;
using Ago.Chat.Infrastructure.Postgres.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Ago.Chat.Infrastructure.Postgres;

/// <summary>`14-12`: the EF adapter for <see cref="IPendingChannelLinkRequestRepository"/> - see that
/// port's own remarks for why it carries two save methods, unlike every other repository in this
/// codebase.</summary>
public sealed class PendingChannelLinkRequestRepository(AgoChatDbContext db) : IPendingChannelLinkRequestRepository
{
    public Task<PendingChannelLinkRequest?> FindLiveAsync(
        SiteId siteId, ChannelKind kind, byte[] codeHash, DateTimeOffset now, CancellationToken cancellationToken) =>
        db.PendingChannelLinkRequests
            .Where(p => p.SiteId == siteId && p.Kind == kind && p.CodeHash == codeHash
                && p.ConsumedAt == null && p.ExpiresAt > now)
            // Most recently created wins on the (astronomically unlikely, but not impossible with a
            // short code) coincidence of two still-live requests sharing one code on the same site and
            // channel - PendingChannelLinkRequest's own remarks on why code hashes are never globally
            // unique.
            .OrderByDescending(p => p.CreatedAt)
            .FirstOrDefaultAsync(cancellationToken);

    public async Task SaveAsync(PendingChannelLinkRequest request, CancellationToken cancellationToken)
    {
        Stage(request);
        await db.SaveChangesAsync(cancellationToken);
    }

    public void Stage(PendingChannelLinkRequest request)
    {
        if (db.Entry(request).State == EntityState.Detached)
        {
            db.PendingChannelLinkRequests.Add(request);
        }
    }
}
