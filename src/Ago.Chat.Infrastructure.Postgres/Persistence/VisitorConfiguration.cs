using Ago.Chat.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Ago.Chat.Infrastructure.Postgres.Persistence;

internal sealed class VisitorConfiguration : IEntityTypeConfiguration<Visitor>
{
    public void Configure(EntityTypeBuilder<Visitor> builder)
    {
        builder.ToTable("visitors");
        builder.HasKey(v => v.Id);
        builder.Property(v => v.Id).HasColumnName("id").HasConversion(IdConverters.Visitor).ValueGeneratedNever();
        builder.Property(v => v.SiteId).HasColumnName("site_id").HasConversion(IdConverters.Site);
        builder.Property(v => v.FirstSeenAt).HasColumnName("first_seen_at");
        builder.Property(v => v.LastSeenAt).HasColumnName("last_seen_at");

        // Not a navigation on Visitor (aggregates stay independent - data-model.md lists site_id as
        // a plain foreign key, never a loaded Site) - HasOne/WithMany with no exposed property is how
        // EF adds the DB-level constraint without adding a Domain reference to Site.
        builder.HasOne<Site>().WithMany().HasForeignKey(v => v.SiteId);
    }
}
