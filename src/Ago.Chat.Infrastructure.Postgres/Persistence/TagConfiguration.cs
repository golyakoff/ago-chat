using Ago.Chat.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Ago.Chat.Infrastructure.Postgres.Persistence;

internal sealed class TagConfiguration : IEntityTypeConfiguration<Tag>
{
    public void Configure(EntityTypeBuilder<Tag> builder)
    {
        builder.ToTable("tags");
        builder.HasKey(t => t.Id);
        builder.Property(t => t.Id).HasColumnName("id").HasConversion(IdConverters.Tag).ValueGeneratedNever();
        builder.Property(t => t.SiteId).HasColumnName("site_id").HasConversion(IdConverters.Site);
        builder.Property(t => t.Name).HasColumnName("name").HasMaxLength(Tag.MaxNameLength).IsRequired();
        builder.Property(t => t.CreatedAt).HasColumnName("created_at");

        // Required FK, EF's default ON DELETE CASCADE - the one line SiteErasureQuery.DeleteSiteAsync
        // relies on for this table (its own remarks: "one statement, relying on the schema's cascades
        // for everything still attached"). By the time that DELETE runs, every conversation for the
        // site is already gone (HasAnyConversationAsync gates it), so conversation_tags rows naming
        // this site's tags are already empty too - this cascade only ever has the tag *definitions*
        // left to remove.
        builder.HasOne<Site>().WithMany().HasForeignKey(t => t.SiteId).OnDelete(DeleteBehavior.Cascade);

        // Case-sensitive at the database - ITagRepository.GetByNameAsync's own remarks on why the
        // case-insensitive guard is only a best-effort pre-check, not this index.
        builder.HasIndex(t => new { t.SiteId, t.Name }).IsUnique().HasDatabaseName("ix_tags_site_name");
    }
}
