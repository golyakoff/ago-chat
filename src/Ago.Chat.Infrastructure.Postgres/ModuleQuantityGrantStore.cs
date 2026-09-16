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
public sealed class ModuleQuantityGrantStore(AgoChatDbContext db, IOutboxWriter outbox, IIdGenerator idGenerator, IClock clock)
    : IModuleQuantityGrantStore
{
    public async Task<int> GetQuantityAsync(SiteId siteId, ModuleKey moduleKey, CancellationToken cancellationToken)
    {
        var grant = await db.ModuleQuantityGrants.AsNoTracking()
            .FirstOrDefaultAsync(g => g.SiteId == siteId && g.ModuleKey == moduleKey, cancellationToken);
        // `23-86`: EffectiveQuantity, not Quantity - see IModuleQuantityGrantStore.GetQuantityAsync's
        // own remarks for why every caller already means the OR'd answer. `25-115`: EffectiveQuantity
        // now needs a clock to decide whether an unconditional grant's own expiry has passed - clock.UtcNow,
        // never DateTimeOffset.UtcNow (CLAUDE.md rule 11), the identical IClock every other Infrastructure
        // adapter in this file already receives for its own now.
        return grant?.EffectiveQuantity(clock.UtcNow) ?? 0;
    }

    public async Task<IReadOnlyDictionary<ModuleKey, int>> GetAllForSiteAsync(
        SiteId siteId, CancellationToken cancellationToken)
    {
        var grants = await db.ModuleQuantityGrants.AsNoTracking()
            .Where(g => g.SiteId == siteId)
            .ToListAsync(cancellationToken);
        var now = clock.UtcNow;
        return grants.ToDictionary(g => g.ModuleKey, g => g.EffectiveQuantity(now));
    }

    public async Task<IReadOnlyList<ModuleQuantityGrant>> GetGrantsForSiteAsync(
        SiteId siteId, CancellationToken cancellationToken) =>
        await db.ModuleQuantityGrants.AsNoTracking()
            .Where(g => g.SiteId == siteId)
            .ToListAsync(cancellationToken);

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

        // `23-86`: grant.EffectiveQuantity, not the caller's own raw `quantity` - see this method's own
        // remarks on IModuleQuantityGrantStore for why this is the one place the OR happens, so a
        // billing-driven revoke (SubscriptionRenewalApplier, unchanged by this item) can never publish
        // "revoked" while the platform owner's own unconditional-grant flag is still set on this row.
        outbox.Enqueue(ModuleQuantityGrantedMapper.ToEnvelope(
            siteId.Value, moduleKey.Value, grant.EffectiveQuantity(now), now, idGenerator));

        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task SetUnconditionalGrantAsync(
        SiteId siteId, ModuleKey moduleKey, bool unconditionallyGranted, string setBy, string reason,
        DateTimeOffset now, CancellationToken cancellationToken, DateTimeOffset? expiresAt = null)
    {
        var grant = await db.ModuleQuantityGrants
            .FirstOrDefaultAsync(g => g.SiteId == siteId && g.ModuleKey == moduleKey, cancellationToken);

        if (grant is null)
        {
            // `23-86` case 1: a trial the owner grants by hand, before any billing quantity was ever
            // written for this (site, module) - the row is created with quantity zero, exactly as if a
            // GrantAsync(..., 0, ...) had run first, so EffectiveQuantity below reads correctly off it.
            grant = ModuleQuantityGrant.Grant(siteId, moduleKey, 0, now);
            db.ModuleQuantityGrants.Add(grant);
        }

        grant.SetUnconditionalGrant(unconditionallyGranted, setBy, reason, now, expiresAt);

        // Republished even though Quantity itself did not change here - EffectiveQuantity can still
        // move (the flag is one of its two inputs), and every downstream consumer of
        // ModuleQuantityGranted trusts the published number as the current fact (this method's own
        // remarks on IModuleQuantityGrantStore.GrantAsync state the identical reasoning).
        outbox.Enqueue(ModuleQuantityGrantedMapper.ToEnvelope(
            siteId.Value, moduleKey.Value, grant.EffectiveQuantity(now), now, idGenerator));

        await db.SaveChangesAsync(cancellationToken);
    }
}
