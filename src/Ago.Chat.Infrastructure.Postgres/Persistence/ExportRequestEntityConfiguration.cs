using Ago.Chat.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Ago.Chat.Infrastructure.Postgres.Persistence;

internal sealed class ExportRequestEntityConfiguration : IEntityTypeConfiguration<ExportRequestEntity>
{
    public void Configure(EntityTypeBuilder<ExportRequestEntity> builder)
    {
        builder.ToTable("export_requests");
        builder.HasKey(e => e.Id);
        builder.Property(e => e.Id).HasColumnName("id").ValueGeneratedNever();
        builder.Property(e => e.SiteId).HasColumnName("site_id").HasConversion(IdConverters.Site);
        builder.Property(e => e.RequestedBy).HasColumnName("requested_by");
        builder.Property(e => e.Status).HasColumnName("status").IsRequired();
        builder.Property(e => e.ObjectKey).HasColumnName("object_key");
        builder.Property(e => e.FailureReason).HasColumnName("failure_reason");
        builder.Property(e => e.RequestedAt).HasColumnName("requested_at");
        builder.Property(e => e.CompletedAt).HasColumnName("completed_at");

        // Cascades with the site - an export request for a site that no longer exists (16-02's own
        // erasure job deleted it) is not a record worth keeping around; `16-02`'s SiteErasureJob
        // already waits for every conversation to drain before deleting the site row, so this cascade
        // never races a still-uploading archive against the row naming its own site.
        builder.HasOne<Site>().WithMany().HasForeignKey(e => e.SiteId).OnDelete(DeleteBehavior.Cascade);

        // Serves SiteExportQuery.ListPendingAsync's claim query - the same partial-index-on-a-queue
        // shape ix_sites_erasure_pending/ix_conversations_erasure_pending already establish.
        builder.HasIndex(e => e.RequestedAt)
            .HasDatabaseName("ix_export_requests_pending")
            .HasFilter("status = 'Pending'");

        // No separate index for IExportRequestRepository.GetAsync's (id, site_id) read: `id` is
        // already the primary key and therefore already an index on its own, and `site_id` there is a
        // post-lookup filter on the one row the PK found, not a predicate that needs its own index -
        // unlike ix_conversations_site_all, which exists for a genuinely site-wide *list* query this
        // item does not add. The FK above still gives site_id its own index implicitly (Npgsql EF's
        // convention for every FK column), which is enough for the cascade delete's own lookup.
    }
}
