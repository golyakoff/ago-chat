using Ago.Chat.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Ago.Chat.Infrastructure.Postgres.Persistence;

/// <summary>`23-88`: "site X asked how many of module K's own things candidate quantity Q would
/// exceed" - one row per (site, module), the identical natural-key shape
/// <see cref="ModuleQuantityGrantConfiguration"/> already uses for its own sibling.</summary>
internal sealed class ModuleQuantityImpactPreviewConfiguration : IEntityTypeConfiguration<ModuleQuantityImpactPreview>
{
    public void Configure(EntityTypeBuilder<ModuleQuantityImpactPreview> builder)
    {
        builder.ToTable("module_quantity_impact_previews");
        builder.HasKey(p => new { p.SiteId, p.ModuleKey });

        builder.Property(p => p.SiteId).HasColumnName("site_id").HasConversion(IdConverters.Site);
        builder.Property(p => p.ModuleKey).HasColumnName("module_key")
            .HasMaxLength(ModuleKey.MaxLength).HasConversion(IdConverters.ModuleKey);
        builder.Property(p => p.RequestedQuantity).HasColumnName("requested_quantity");
        builder.Property(p => p.RequestedAt).HasColumnName("requested_at").HasColumnType("timestamptz");
        builder.Property(p => p.AffectedCount).HasColumnName("affected_count");
        builder.Property(p => p.AffectedItemDisplayNames).HasColumnName("affected_item_display_names")
            .HasConversion(AffectedItemDisplayNamesConverter.Instance, AffectedItemDisplayNamesConverter.Comparer);
        builder.Property(p => p.AnsweredAt).HasColumnName("answered_at").HasColumnType("timestamptz");

        builder.HasOne<Site>().WithMany().HasForeignKey(p => p.SiteId);
    }
}
