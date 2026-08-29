using Ago.Chat.Application.Abstractions;
using Ago.Chat.Domain;
using Ago.Chat.Infrastructure.Postgres.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Ago.Chat.Infrastructure.Postgres;

/// <summary>`20-07`: the EF adapter for <see cref="IEnabledModuleRepository"/> - the same
/// detached-means-insert shape <see cref="ChannelCredentialRepository"/> already establishes.</summary>
public sealed class EnabledModuleRepository(AgoChatDbContext db) : IEnabledModuleRepository
{
    public Task<EnabledModule?> GetAsync(SiteId siteId, ModuleKey moduleKey, CancellationToken cancellationToken) =>
        db.EnabledModules.FirstOrDefaultAsync(m => m.SiteId == siteId && m.ModuleKey == moduleKey, cancellationToken);

    public async Task SaveAsync(EnabledModule module, CancellationToken cancellationToken)
    {
        if (db.Entry(module).State == EntityState.Detached)
        {
            db.EnabledModules.Add(module);
        }

        await db.SaveChangesAsync(cancellationToken);
    }
}
