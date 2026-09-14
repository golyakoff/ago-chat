using Ago.Chat.Application.Abstractions;
using Ago.Chat.Domain;
using Ago.Chat.Infrastructure.Postgres.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Ago.Chat.Infrastructure.Postgres;

/// <summary>`25-04`: EF adapter for <see cref="IAiAddOnEnablementRepository"/>. The upsert is
/// <see cref="DbSet{TEntity}.Add"/>-if-untracked, exactly as the natural-key aggregates around it do -
/// <see cref="AiAddOnEnablement.ForSite"/> is only ever called by a handler that has already looked and
/// found nothing, so a duplicate insert would be a primary-key violation rather than a silent second
/// row.</summary>
public sealed class AiAddOnEnablementRepository(AgoChatDbContext db) : IAiAddOnEnablementRepository
{
    public async Task<AiAddOnEnablement?> GetForSiteAsync(SiteId siteId, CancellationToken cancellationToken) =>
        await db.AiAddOnEnablements.FirstOrDefaultAsync(e => e.SiteId == siteId, cancellationToken);

    public async Task SaveAsync(AiAddOnEnablement enablement, CancellationToken cancellationToken)
    {
        if (db.Entry(enablement).State == EntityState.Detached)
        {
            db.AiAddOnEnablements.Add(enablement);
        }

        await db.SaveChangesAsync(cancellationToken);
    }
}
