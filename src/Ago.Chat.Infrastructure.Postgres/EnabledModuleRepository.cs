using Ago.Chat.Application.Abstractions;
using Ago.Chat.Domain;
using Ago.Chat.Infrastructure.Postgres.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Ago.Chat.Infrastructure.Postgres;

/// <summary>`20-07`: the EF adapter for <see cref="IEnabledModuleRepository"/> - the same
/// detached-means-insert shape <see cref="ChannelCredentialRepository"/> already establishes.</summary>
public sealed class EnabledModuleRepository(AgoChatDbContext db) : IEnabledModuleRepository
{
    // `22-11`: AsNoTracking - RotateModuleCredentialHandler/RevokeModuleForSiteHandler/
    // VerifyModuleRegistrationHandler all read through this method and then, on the same DbContext,
    // either call UpdateAsync with a *different* instance carrying the same id
    // (EnabledModule.WithCredential builds a new one rather than mutating in place, since every
    // property here is get-only) or DeleteAsync. A tracked read here would leave two instances of the
    // same row in this context's identity map the moment either of those ran - found failing exactly
    // that way, not by inspection, when RotateModuleCredentialHandlerTests's own real-Postgres sibling
    // in Ago.Chat.Integration.Tests threw "cannot be tracked because another instance with the same
    // key value is already being tracked."
    public Task<EnabledModule?> GetAsync(SiteId siteId, ModuleKey moduleKey, CancellationToken cancellationToken) =>
        db.EnabledModules.AsNoTracking().FirstOrDefaultAsync(m => m.SiteId == siteId && m.ModuleKey == moduleKey, cancellationToken);

    public async Task SaveAsync(EnabledModule module, CancellationToken cancellationToken)
    {
        if (db.Entry(module).State == EntityState.Detached)
        {
            db.EnabledModules.Add(module);
        }

        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task UpdateAsync(EnabledModule module, CancellationToken cancellationToken)
    {
        db.EnabledModules.Update(module);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task DeleteAsync(EnabledModuleId id, CancellationToken cancellationToken)
    {
        await db.EnabledModules.Where(m => m.Id == id).ExecuteDeleteAsync(cancellationToken);
    }
}
