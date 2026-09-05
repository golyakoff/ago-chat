using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Ago.Chat.Infrastructure.Postgres.Persistence;

/// <summary>
/// `23-13`: `module_revoke_overrides` - one row per exercised override, written when the platform owner
/// revokes a tenant's own self-service purchase with the request's force flag set. Never written for an
/// owner revoking their own grant - <see cref="ModuleRevokeOverrideEntity"/>'s own remarks and
/// `RevokeModuleForSiteAsOwnerHandler`'s own remarks state why.
///
/// <para><b>No FK to <c>sites</c>, deliberately - the same reason <c>AccessRecordEntity</c>'s own
/// <c>SiteId</c> carries none, and <c>ErasureRecordEntity</c>'s before that.</b> A tenant whose purchase
/// was overridden and who later closes their account (or is erased) is exactly the tenant most likely
/// to ask, later, "who took this away from me, and why" - a cascading foreign key would let the answer
/// disappear with the account, which is the one outcome this record exists to prevent.</para>
///
/// <para><b>No FK from <c>module_key</c> either</b> - by the time this row is written,
/// <c>enabled_modules</c>' own row for this (site, module) pair has already been deleted
/// (<c>RevokeModuleForSiteAsOwnerHandler</c>'s own module-first-then-delete ordering). There is no live
/// row left to reference; <c>module_key</c> here is a snapshot of what the deleted row named, not a
/// pointer to anything still standing.</para>
/// </summary>
internal sealed class ModuleRevokeOverrideEntityConfiguration : IEntityTypeConfiguration<ModuleRevokeOverrideEntity>
{
    public void Configure(EntityTypeBuilder<ModuleRevokeOverrideEntity> builder)
    {
        builder.ToTable("module_revoke_overrides");
        builder.HasKey(e => e.Id);
        builder.Property(e => e.Id).HasColumnName("id").ValueGeneratedNever();
        // No HasOne<Site>()/HasForeignKey - see this type's and ModuleRevokeOverrideEntity's own
        // remarks for why the absence is deliberate, not a gap.
        builder.Property(e => e.SiteId).HasColumnName("site_id").HasConversion(IdConverters.Site).IsRequired();
        builder.Property(e => e.ModuleKey).HasColumnName("module_key").IsRequired();
        builder.Property(e => e.RevokedBy).HasColumnName("revoked_by").IsRequired();
        builder.Property(e => e.Reason).HasColumnName("reason").IsRequired();
        builder.Property(e => e.RevokedAt).HasColumnName("revoked_at").IsRequired();

        // Not consulted by anything this item builds (no console screen - `23-13`'s own Out of
        // scope), but every table in this codebase carries an index for its own site-scoped read
        // (db-migration skill: "multi-tenancy is not optional... every query filters by it") - the
        // one IModuleRevokeOverrideRepository.ListForSiteAsync needs, and the one a future support
        // screen would need without a migration of its own.
        builder.HasIndex(e => e.SiteId).HasDatabaseName("ix_module_revoke_overrides_site_id");
    }
}
