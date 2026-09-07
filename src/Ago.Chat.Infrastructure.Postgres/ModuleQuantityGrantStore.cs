using Ago.Chat.Application.Abstractions;
using Ago.Chat.Application.Mapping;
using Ago.Chat.Domain;
using Ago.Chat.Infrastructure.Postgres.Persistence;
using Ago.Platform.Abstractions;
using Ago.Platform.Kernel;
using Microsoft.EntityFrameworkCore;

namespace Ago.Chat.Infrastructure.Postgres;

/// <summary>`22-07`'s <see cref="IModuleQuantityGrantStore"/> adapter - one implicit transaction per
/// <see cref="GrantAsync"/> call, the same shape <c>EnabledModuleRepository.SaveAsync</c> uses, widened
/// by exactly one more staged write: the outbox row, so the state change and the event it describes
/// commit or roll back together (rule 4) with no second call for a caller to remember.</summary>
public sealed class ModuleQuantityGrantStore(AgoChatDbContext db, IOutboxWriter outbox, IIdGenerator idGenerator)
    : IModuleQuantityGrantStore
{
    public async Task<int> GetQuantityAsync(SiteId siteId, ModuleKey moduleKey, CancellationToken cancellationToken)
    {
        var grant = await db.ModuleQuantityGrants.AsNoTracking()
            .FirstOrDefaultAsync(g => g.SiteId == siteId && g.ModuleKey == moduleKey, cancellationToken);
        return grant?.Quantity ?? 0;
    }

    public async Task<IReadOnlyDictionary<ModuleKey, int>> GetAllForSiteAsync(
        SiteId siteId, CancellationToken cancellationToken)
    {
        var grants = await db.ModuleQuantityGrants.AsNoTracking()
            .Where(g => g.SiteId == siteId)
            .ToListAsync(cancellationToken);
        return grants.ToDictionary(g => g.ModuleKey, g => g.Quantity);
    }

    public async Task GrantAsync(
        SiteId siteId, ModuleKey moduleKey, int quantity, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var grant = await db.ModuleQuantityGrants
            .FirstOrDefaultAsync(g => g.SiteId == siteId && g.ModuleKey == moduleKey, cancellationToken);

        if (grant is null)
        {
            grant = ModuleQuantityGrant.Grant(siteId, moduleKey, quantity, now);
            db.ModuleQuantityGrants.Add(grant);
        }
        else
        {
            grant.SetQuantity(quantity, now);
        }

        outbox.Enqueue(ModuleQuantityGrantedMapper.ToEnvelope(siteId.Value, moduleKey.Value, quantity, now, idGenerator));

        await db.SaveChangesAsync(cancellationToken);
    }
}
