using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Ago.Chat.Infrastructure.Postgres.Persistence;

/// <summary>
/// `25-77`: `role_permission_removal_overrides` - see <see cref="RolePermissionRemovalOverrideEntity"/>'s
/// own remarks for what this table is and why it carries no foreign key. Registered only through
/// <see cref="AgoChatDbContext.OnModelCreating"/>'s assembly scan (no named <c>DbSet</c> property) -
/// the identical "migration-scaffolding only" shape <c>ModuleRevokeOverrideEntity</c>'s own
/// configuration uses, reachable in code through <c>Set&lt;RolePermissionRemovalOverrideEntity&gt;()</c>
/// only, which is all <see cref="RoleRepository.RemovePermissionsAsync"/> needs.
/// </summary>
internal sealed class RolePermissionRemovalOverrideEntityConfiguration
    : IEntityTypeConfiguration<RolePermissionRemovalOverrideEntity>
{
    public void Configure(EntityTypeBuilder<RolePermissionRemovalOverrideEntity> builder)
    {
        builder.ToTable("role_permission_removal_overrides");
        builder.HasKey(e => e.Id);
        builder.Property(e => e.Id).HasColumnName("id").ValueGeneratedNever();
        // No HasOne<Site>()/HasForeignKey - see this entity's own remarks for why the absence is
        // deliberate, not a gap.
        builder.Property(e => e.SiteId).HasColumnName("site_id").HasConversion(IdConverters.Site).IsRequired();
        builder.Property(e => e.RoleName).HasColumnName("role_name").IsRequired();
        // The same native Npgsql List<string> -> text[] mapping RoleRecordConfiguration's own
        // Permissions column already uses - no join table needed for a handful of permission strings.
        builder.Property(e => e.Permissions).HasColumnName("permissions").IsRequired();
        builder.Property(e => e.RemovedBy).HasColumnName("removed_by").IsRequired();
        builder.Property(e => e.Reason).HasColumnName("reason").IsRequired();
        builder.Property(e => e.RemovedAt).HasColumnName("removed_at").IsRequired();

        // Not consulted by anything this item builds (no console screen - matching `module_revoke_
        // overrides`' own out-of-scope), but every table in this codebase carries an index for its own
        // site-scoped read (db-migration skill: "multi-tenancy is not optional") - what a future
        // support screen, or this item's own integration test, needs without a migration of its own.
        builder.HasIndex(e => e.SiteId).HasDatabaseName("ix_role_permission_removal_overrides_site_id");
    }
}
