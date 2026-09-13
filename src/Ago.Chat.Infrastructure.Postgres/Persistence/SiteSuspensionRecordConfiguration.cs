using Ago.Chat.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Ago.Chat.Infrastructure.Postgres.Persistence;

internal sealed class SiteSuspensionRecordConfiguration : IEntityTypeConfiguration<SiteSuspensionRecordEntity>
{
    public void Configure(EntityTypeBuilder<SiteSuspensionRecordEntity> builder)
    {
        builder.ToTable("site_suspensions");
        builder.HasKey(e => e.Id);
        builder.Property(e => e.Id).HasColumnName("id").ValueGeneratedNever();
        builder.Property(e => e.SiteId).HasColumnName("site_id").HasConversion(IdConverters.Site).IsRequired();
        builder.Property(e => e.Action).HasColumnName("action").IsRequired();
        builder.Property(e => e.PerformedBy).HasColumnName("performed_by").IsRequired();
        builder.Property(e => e.Reason).HasColumnName("reason").IsRequired();
        builder.Property(e => e.SuspendedUntil).HasColumnName("suspended_until");
        builder.Property(e => e.PerformedAt).HasColumnName("performed_at").IsRequired();

        // Ordinary tenant data - real FK, cascading with the site, the identical default EF convention
        // RoleChangeRecordConfiguration's own remarks describe for the identical reason: this table has
        // no reason to survive the tenant's own erasure.
        builder.HasOne<Site>().WithMany().HasForeignKey(e => e.SiteId);

        // `ListForOwnerAsync`'s own "most recent row per site" read walks this index - newest first,
        // per site, the identical "index the columns a keyset/latest-row read needs together" reasoning
        // RoleChangeRecordConfiguration's own ix_role_change_records_site_id_changed_at already states.
        builder.HasIndex(e => new { e.SiteId, e.PerformedAt }).HasDatabaseName("ix_site_suspensions_site_id_performed_at");
    }
}
