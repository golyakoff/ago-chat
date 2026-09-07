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
    // `22-30`: excludes a revoked row - a revoke no longer deletes (EnabledModule.RevokedAt's own
    // remarks), so without this filter a site that revoked a module and re-enabled it would carry two
    // rows for the same (SiteId, ModuleKey), and this method's own lack of an ORDER BY would make
    // which one Rotate/Revoke/Verify act on non-deterministic. Filtering here is what keeps this
    // method's external answer - "the current registration for this site and module, or none" -
    // identical to what it already was when a revoke genuinely removed the row.
    public Task<EnabledModule?> GetAsync(SiteId siteId, ModuleKey moduleKey, CancellationToken cancellationToken) =>
        db.EnabledModules.AsNoTracking()
            .FirstOrDefaultAsync(m => m.SiteId == siteId && m.ModuleKey == moduleKey && m.RevokedAt == null, cancellationToken);

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
