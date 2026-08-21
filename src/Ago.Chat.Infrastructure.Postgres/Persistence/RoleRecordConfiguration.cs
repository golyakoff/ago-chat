using Ago.Chat.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Ago.Chat.Infrastructure.Postgres.Persistence;

internal sealed class RoleRecordConfiguration : IEntityTypeConfiguration<RoleRecord>
{
    public void Configure(EntityTypeBuilder<RoleRecord> builder)
    {
        builder.ToTable("roles");
        builder.HasKey(r => r.Id);
        builder.Property(r => r.Id).HasColumnName("id").ValueGeneratedNever();
        builder.Property(r => r.SiteId).HasColumnName("site_id").HasConversion(IdConverters.Site);
        builder.Property(r => r.Name).HasColumnName("name").IsRequired();
        // Postgres text[] via the Npgsql provider's native List<string> support - no join table
        // needed for a handful of permission strings per role.
        builder.Property(r => r.Permissions).HasColumnName("permissions");

        builder.HasOne<Site>().WithMany().HasForeignKey(r => r.SiteId);
    }
}
