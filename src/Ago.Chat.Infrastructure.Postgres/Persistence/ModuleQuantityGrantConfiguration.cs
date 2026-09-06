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

        builder.HasOne<Site>().WithMany().HasForeignKey(g => g.SiteId);
    }
}
