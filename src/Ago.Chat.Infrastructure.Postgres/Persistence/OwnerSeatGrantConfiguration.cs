using Ago.Chat.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Ago.Chat.Infrastructure.Postgres.Persistence;

/// <summary>`25-181`: "site X holds an owner-granted extra of role R, quantity Q" - one row per
/// (site, role), keyed by the natural pair, the identical shape <see cref="ModuleQuantityGrantConfiguration"/>
/// already establishes for its own (site, module) key.</summary>
internal sealed class OwnerSeatGrantConfiguration : IEntityTypeConfiguration<OwnerSeatGrant>
{
    public void Configure(EntityTypeBuilder<OwnerSeatGrant> builder)
    {
        builder.ToTable("owner_seat_grants");
        builder.HasKey(g => new { g.SiteId, g.Role });

        builder.Property(g => g.SiteId).HasColumnName("site_id").HasConversion(IdConverters.Site);
        builder.Property(g => g.Role).HasColumnName("role").HasConversion<string>().HasMaxLength(20);
        builder.Property(g => g.Quantity).HasColumnName("quantity");
        builder.Property(g => g.GrantedBy).HasColumnName("granted_by");
        builder.Property(g => g.Reason).HasColumnName("reason");
        builder.Property(g => g.GrantedAt).HasColumnName("granted_at").HasColumnType("timestamptz");
        builder.Property(g => g.ExpiresAt).HasColumnName("expires_at").HasColumnType("timestamptz");

        builder.HasOne<Site>().WithMany().HasForeignKey(g => g.SiteId);
    }
}
