using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Ago.Chat.Infrastructure.Postgres.Persistence;

/// <summary>
/// `23-11`: `contact_reveals` - one row per deliberate unmasking of a visitor contact detail,
/// deliberately holding nothing about the value that was revealed. Every column is checked against
/// that question, the same discipline <see cref="AccessRecordEntityConfiguration"/>'s own remarks
/// apply to `access_records`.
/// </summary>
internal sealed class ContactRevealEntityConfiguration : IEntityTypeConfiguration<ContactRevealEntity>
{
    public void Configure(EntityTypeBuilder<ContactRevealEntity> builder)
    {
        builder.ToTable("contact_reveals");
        builder.HasKey(e => e.Id);
        builder.Property(e => e.Id).HasColumnName("id").ValueGeneratedNever();
        builder.Property(e => e.OccurredAt).HasColumnName("occurred_at").IsRequired();
        // No HasOne<Site>()/HasForeignKey - see ContactRevealEntity's own remarks for why the absence
        // is deliberate, not a gap.
        builder.Property(e => e.SiteId).HasColumnName("site_id").HasConversion(IdConverters.Site).IsRequired();
        builder.Property(e => e.ConversationId).HasColumnName("conversation_id").IsRequired();
        builder.Property(e => e.ContactDetailId).HasColumnName("contact_detail_id").IsRequired();
        builder.Property(e => e.OperatorId).HasColumnName("operator_id").IsRequired();
        builder.Property(e => e.Surface).HasColumnName("surface").IsRequired();

        // Serves IContactRevealRepository.ListForSiteAsync's own keyset read - the identical
        // "index the columns the WHERE and ORDER BY actually use together" reasoning
        // AccessRecordEntityConfiguration's own remarks give for its own index.
        builder.HasIndex(e => new { e.SiteId, e.Id })
            .HasDatabaseName("ix_contact_reveals_site_id_id");
    }
}
