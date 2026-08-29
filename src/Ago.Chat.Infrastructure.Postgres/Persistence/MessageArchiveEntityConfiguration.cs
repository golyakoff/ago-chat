using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Ago.Chat.Infrastructure.Postgres.Persistence;

internal sealed class MessageArchiveEntityConfiguration : IEntityTypeConfiguration<MessageArchiveEntity>
{
    public void Configure(EntityTypeBuilder<MessageArchiveEntity> builder)
    {
        builder.ToTable("message_archives");
        builder.HasKey(e => e.Id);
        builder.Property(e => e.Id).HasColumnName("id").ValueGeneratedNever();
        builder.Property(e => e.SiteId).HasColumnName("site_id").HasConversion(IdConverters.Site);
        builder.Property(e => e.RetentionClass).HasColumnName("retention_class").IsRequired();
        builder.Property(e => e.PeriodStart).HasColumnName("period_start");
        builder.Property(e => e.PeriodEnd).HasColumnName("period_end");
        builder.Property(e => e.ObjectKey).HasColumnName("object_key").IsRequired();
        builder.Property(e => e.ArchivedAt).HasColumnName("archived_at");

        // Cascades with the site, the same reasoning ExportRequestEntityConfiguration's own comment
        // gives: an archive manifest row naming a site that no longer exists is not worth keeping -
        // 16-02's SiteErasureJob already drains every conversation (and, after this item, every
        // message partition this site could still be waiting on) before deleting the site row.
        builder.HasOne<Ago.Chat.Domain.Site>().WithMany().HasForeignKey(e => e.SiteId).OnDelete(DeleteBehavior.Cascade);

        // The idempotency key MessageArchiveJob relies on (IMessageArchiveRepository.RecordAsync's own
        // remarks: "a retry after a crash mid-cycle must not double-write") and the exact triple
        // IMessageArchiveGate's real implementation and the tenant-facing lookup both key on.
        builder.HasIndex(e => new { e.SiteId, e.RetentionClass, e.PeriodStart })
            .IsUnique()
            .HasDatabaseName("ux_message_archives_site_class_period");
    }
}
