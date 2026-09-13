using Ago.Chat.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Ago.Chat.Infrastructure.Postgres.Persistence;

/// <summary>`22-07`: "site X's module K has quantity Q" - one row per (site, module), keyed by the
/// natural pair rather than a synthetic id (<see cref="ModuleQuantityGrant"/>'s own remarks).</summary>
internal sealed class ModuleQuantityGrantConfiguration : IEntityTypeConfiguration<ModuleQuantityGrant>
{
    public void Configure(EntityTypeBuilder<ModuleQuantityGrant> builder)
    {
        builder.ToTable("module_quantity_grants");
        builder.HasKey(g => new { g.SiteId, g.ModuleKey });

        builder.Property(g => g.SiteId).HasColumnName("site_id").HasConversion(IdConverters.Site);
        builder.Property(g => g.ModuleKey).HasColumnName("module_key")
            .HasMaxLength(ModuleKey.MaxLength).HasConversion(IdConverters.ModuleKey);
        builder.Property(g => g.Quantity).HasColumnName("quantity");
        builder.Property(g => g.GrantedAt).HasColumnName("granted_at").HasColumnType("timestamptz");

        // `23-86`: the platform owner's own unconditional-grant flag and its provenance - see
        // ModuleQuantityGrant's own remarks for why this is a second, independent input rather than a
        // second write to Quantity above.
        builder.Property(g => g.UnconditionallyGrantedByOwner).HasColumnName("unconditionally_granted_by_owner");
        builder.Property(g => g.UnconditionalGrantSetBy).HasColumnName("unconditional_grant_set_by");
        builder.Property(g => g.UnconditionalGrantReason).HasColumnName("unconditional_grant_reason");
        builder.Property(g => g.UnconditionalGrantSetAt).HasColumnName("unconditional_grant_set_at").HasColumnType("timestamptz");

        // EffectiveQuantity is a computed read, never a column - see its own remarks for why storing it
        // would just be a second place the OR'd answer could drift from its two real inputs.
        builder.Ignore(g => g.EffectiveQuantity);

        builder.HasOne<Site>().WithMany().HasForeignKey(g => g.SiteId);
    }
}
