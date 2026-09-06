using Ago.Chat.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Ago.Chat.Infrastructure.Postgres.Persistence;

/// <summary>
/// `23-07`: `site_widget_activity` - the funnel's own per-day counter table.
/// <see cref="SiteWidgetActivityEntity"/>'s own remarks explain why this is migration-scaffolding
/// only; this file is what tells `dotnet ef migrations add` the composite key an upsert needs.
/// </summary>
internal sealed class SiteWidgetActivityEntityConfiguration : IEntityTypeConfiguration<SiteWidgetActivityEntity>
{
    public void Configure(EntityTypeBuilder<SiteWidgetActivityEntity> builder)
    {
        builder.ToTable("site_widget_activity");

        // Composite, not a surrogate `id` - the same shape ConversationTagRecordConfiguration already
        // uses for its own join row. `WidgetActivityWriter`'s `ON CONFLICT (site_id, day)` is the
        // whole reason this table exists in this shape: a natural key it can upsert against, with no
        // read-then-write round trip of its own.
        builder.HasKey(e => new { e.SiteId, e.Day });
        builder.Property(e => e.SiteId).HasColumnName("site_id").HasConversion(IdConverters.Site);
        builder.Property(e => e.Day).HasColumnName("day").HasColumnType("date");
        builder.Property(e => e.Loads).HasColumnName("loads").HasDefaultValue(0);
        builder.Property(e => e.Opens).HasColumnName("opens").HasDefaultValue(0);
        builder.Property(e => e.Conversations).HasColumnName("conversations").HasDefaultValue(0);

        // Cascades with the site - an approximate, throwaway dashboard row naming a site that no
        // longer exists is not worth keeping, the identical reasoning MessageArchiveEntityConfiguration's
        // own remarks give for its own FK.
        builder.HasOne<Site>().WithMany().HasForeignKey(e => e.SiteId).OnDelete(DeleteBehavior.Cascade);

        // No secondary index: every real query (the flush's own upsert, and GetTotalsAsync's own
        // `WHERE site_id = @SiteId AND day >= @Since`) is served by the primary key itself, whose
        // leading column is `site_id` - `caching.md`'s own "never index ahead of a real query" rule,
        // restated for a brand-new table instead of an existing one.
    }
}
