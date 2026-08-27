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
        // `11-10`: a second check constraint on the same table, added as a second statement in this
        // block rather than a second `ToTable` call - `TableBuilder.HasCheckConstraint` can be invoked
        // any number of times against the same `t`, and EF folds every call into the one table's
        // constraint list regardless of how many separate `HasCheckConstraint` calls produced them.
        builder.ToTable("sites", t =>
        {
            t.HasCheckConstraint("ck_sites_widget_position", "widget_position IN ('bottom-right', 'bottom-left')");
            t.HasCheckConstraint("ck_sites_widget_locale", "widget_locale IN ('en', 'ru')");
        });
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

        // `8-07`: null for every ordinary tenant and for the seeded `8-05` demo sites, which are not
        // created on demand and must not expire. The partial index lives in the migration rather than
        // here - EF can express `HasFilter`, but the filter string would then be duplicated between the
        // model and the SQL, and only one of the two is what Postgres actually runs.
        builder.Property(s => s.DemoExpiresAt).HasColumnName("demo_expires_at");
        builder.HasIndex(s => s.DemoExpiresAt)
            .HasDatabaseName("ix_sites_demo_expiry")
            .HasFilter("demo_expires_at is not null");

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

        // `14-04`: same shape again - three private backing fields, three columns, the computed
        // OfflineAutoReply value object ignored so the columns stay the one source of truth. Both
        // scalar columns get a database default matching OfflineAutoReplySettings.Disabled, so every
        // row written before this migration (the seeded demo tenants included) reads back as "off,
        // nothing to say" rather than needing a backfill.
        builder.Property<bool>("_offlineAutoReplyEnabled")
            .HasColumnName("offline_auto_reply_enabled")
            .HasDefaultValue(false);
        builder.Property<string>("_offlineAutoReplyFallback")
            .HasColumnName("offline_auto_reply_fallback")
            .IsRequired()
            .HasDefaultValue(string.Empty);
        builder.Property<List<OfflineAutoReplyRule>?>("_offlineAutoReplyRules")
            .HasColumnName("offline_auto_reply_rules")
            .HasConversion(OfflineAutoReplyConverters.Rules, OfflineAutoReplyConverters.RulesComparer);
        builder.Ignore(s => s.OfflineAutoReply);

        // `11-10`: same "computed property over a private field EF is pointed at by name" shape again -
        // one backing field, one column, the CHECK constraint declared above as this table's second one.
        builder.Property<Locale>("_locale").HasColumnName("widget_locale")
            .HasConversion(LocaleConverter.Instance)
            .HasDefaultValue(Locale.En);
        builder.Ignore(s => s.Locale);
    }
}
