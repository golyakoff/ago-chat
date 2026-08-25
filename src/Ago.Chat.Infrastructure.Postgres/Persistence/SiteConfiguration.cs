using Ago.Chat.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Ago.Chat.Infrastructure.Postgres.Persistence;

internal sealed class SiteConfiguration : IEntityTypeConfiguration<Site>
{
    public void Configure(EntityTypeBuilder<Site> builder)
    {
        // `11-01`: the CHECK constraint backstops WidgetConfig's own hex-format/enum validation at the
        // storage level (db-migration skill: "anything enforcing a guarantee gets a constraint, not
        // just application code") - EF generates the matching migrationBuilder.AddCheckConstraint call
        // from this declaration (Stage11AddSiteWidgetConfig), so the constraint's SQL lives in exactly
        // one place, not duplicated by hand in the migration too.
        builder.ToTable("sites", t => t.HasCheckConstraint(
            "ck_sites_widget_position", "widget_position IN ('bottom-right', 'bottom-left')"));
        builder.HasKey(s => s.Id);
        builder.Property(s => s.Id).HasColumnName("id").HasConversion(IdConverters.Site).ValueGeneratedNever();
        builder.Property(s => s.PublicKey).HasColumnName("public_key").IsRequired();
        // `10-02`: additive, nullable-at-the-database-level via a default rather than a backfill -
        // no existing row (the demo site included, seeded outside this codebase's own migrations by
        // `ago-deploy/seed/create-demo-tenant.sh`) had a name before this column existed.
        builder.Property(s => s.Name).HasColumnName("name").IsRequired().HasDefaultValue(string.Empty);
        // `12-02`: nullable with no database default - deliberately *not* `HasDefaultValueSql("now()")`.
        // A default would both stamp every pre-existing row at migration time (see Site.CreatedAt on
        // why that is a fabricated value, not a convenience) and put the row's creation time on the
        // database's clock instead of `IClock`'s (`CLAUDE.md` rule 11).
        builder.Property(s => s.CreatedAt).HasColumnName("created_at");

        // AllowedOrigins is a computed property (IReadOnlyList<string>) over a private List<string>
        // field - Site never exposes a settable collection, so EF is pointed at the field directly.
        builder.Property<List<string>>("_allowedOrigins").HasColumnName("allowed_origins");
        builder.Ignore(s => s.AllowedOrigins);

        // `11-01`: same "computed property over a private field EF is pointed at by name" shape as
        // AllowedOrigins above - WidgetConfig itself (Site.cs's own remarks) is never mapped directly,
        // each backing field gets its own column instead.
        builder.Property<string?>("_widgetPrimaryColorHex").HasColumnName("widget_primary_color_hex");
        builder.Property<Position>("_widgetPosition").HasColumnName("widget_position")
            .HasConversion(PositionConverter.Instance)
            .HasDefaultValue(Position.BottomRight);
        builder.Ignore(s => s.WidgetConfig);
    }
}
