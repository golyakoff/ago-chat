using Ago.Chat.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Ago.Chat.Infrastructure.Postgres.Persistence;

internal sealed class OperatorConfiguration : IEntityTypeConfiguration<Operator>
{
    public void Configure(EntityTypeBuilder<Operator> builder)
    {
        builder.ToTable("operators");
        builder.HasKey(o => o.Id);
        builder.Property(o => o.Id).HasColumnName("id").HasConversion(IdConverters.Operator).ValueGeneratedNever();
        builder.Property(o => o.SiteId).HasColumnName("site_id").HasConversion(IdConverters.Site);
        builder.Property(o => o.Status).HasColumnName("status").HasConversion<string>();
        builder.Property(o => o.Capacity).HasColumnName("capacity");

        builder.HasOne<Site>().WithMany().HasForeignKey(o => o.SiteId);
    }
}
