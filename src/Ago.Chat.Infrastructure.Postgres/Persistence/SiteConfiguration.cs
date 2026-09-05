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

        // `16-02`: a shadow property, not a mapped Site property - the same reason
        // OperatorConfiguration's `active_chats` is a shadow property rather than one on Operator. This
        // column has exactly one legitimate writer (RequestSiteErasureHandler, via
        // IErasureRequestRepository's own targeted UPDATE) and is read only by SiteErasureJob's
        // bounded-batch claim query, both raw Npgsql/Dapper - never through Site's own
        // load-mutate-SaveChangesAsync path. Site aggregate loads never touch this table's write-heavy
        // columns (WidgetConfig, OfflineAutoReply), but a mapped property here would still tempt a
        // future caller into exactly the EF-load-races-raw-SQL failure mode the shadow-property split
        // exists to avoid, and nothing in Site's own behaviour ever needs to reason about "am I pending
        // erasure" - the aggregate is never consulted again once erasure starts; the job deletes rows
        // directly. See IErasureRequestRepository's own remarks for the rest of this reasoning.
        builder.Property<DateTimeOffset?>("ErasureRequestedAt").HasColumnName("erasure_requested_at");
        builder.HasIndex("ErasureRequestedAt")
            .HasDatabaseName("ix_sites_erasure_pending")
            .HasFilter("erasure_requested_at is not null");

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
        // `16-04`: two more backing fields, same shape - no CHECK constraint, unlike widget_position/
        // widget_locale above: free text and a URL are not a closed set SQL can enumerate, the same
        // boundary `18-03`'s canned_responses comment already draws for its own free-text pair.
        // WidgetConfig's own constructor is this value's only validation, same as CannedResponse's.
        builder.Property<string?>("_widgetNoticeText").HasColumnName("widget_notice_text");
        builder.Property<string?>("_widgetNoticeUrl").HasColumnName("widget_notice_url");
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

        // `18-03`: same shape again - one backing field, one column, no check constraint (unlike
        // WidgetConfig/OfflineAutoReply, nothing here is a closed enum-like set of legal values a CHECK
        // could enforce; CannedResponse's own constructor is the only validation this value has, the
        // same "the constraint enforces a fact SQL itself can express" boundary widget_position/
        // widget_locale's own comments draw - a free-text title/body pair isn't such a fact).
        builder.Property<List<CannedResponse>?>("_cannedResponses")
            .HasColumnName("canned_responses")
            .HasConversion(CannedResponseConverters.Responses, CannedResponseConverters.ResponsesComparer);
        builder.Ignore(s => s.CannedResponses);

        // `13-01`/`13-02`: ordinary mapped properties, not computed-over-a-private-field like every
        // column above - `Site.Tier`/`Site.SeatLimit` are plain auto-properties with a private setter
        // (13-02's `Site.ActivateSubscription` is that setter's first real caller), so there is nothing
        // for a backing field to buy here the way WidgetConfig/OfflineAutoReply's richer value objects
        // need one; EF Core maps a private setter directly, no `Property<T>("_field")` indirection
        // required. Both default at the database level too (Stage13AddSiteTierAndSeatLimit), so every
        // existing row reads back on the free tier without a backfill, the same "additive column,
        // database default, no migration touches existing data" shape `Name`'s own remarks already
        // established.
        //
        // `13-08`: `seat_limit`'s own default raised from `1` to `2` (`Stage13RaiseFreeTierSeatLimit`) -
        // unlike the columns above, this one *does* need a backfill, because a database default only
        // ever applies to a row inserted after the change; every row already in `sites` still read `1`
        // until that migration's own `UPDATE` ran. Stated here rather than left looking like the same
        // "no backfill needed" shape the paragraph above describes, because it is not.
        builder.Property(s => s.Tier).HasColumnName("tier").IsRequired().HasDefaultValue("free");
        builder.Property(s => s.SeatLimit).HasColumnName("seat_limit").HasDefaultValue(2);

        // `23-06`: four shadow properties, the identical shape `ErasureRequestedAt` already
        // establishes just above for the identical reason - each has exactly one legitimate writer
        // (`ISiteInstallationSignalRepository`, via raw Npgsql conditional `UPDATE`s) and is read only
        // through that same port's own `GetAsync`, never through `Site`'s load-mutate-SaveChangesAsync
        // path. Routing a visitor-session mint's sighting through the aggregate would mean loading the
        // whole `Site` (widget config, offline auto-reply, canned responses and all) just to move one
        // timestamp forward, on the single hottest, highest-concurrency write path in this product.
        //
        // No CHECK constraint on any of the four - unlike `widget_position`/`widget_locale` above,
        // none of these is a closed enum-like set: three are plain timestamps and
        // `last_refused_origin` is free text (an Origin header value), the same "nothing here for SQL
        // to enumerate" boundary `widget_notice_text`/`widget_notice_url`'s own comment already draws.
        //
        // All nullable, no database default, no backfill - `12-02`'s own `CreatedAt` precedent:
        // stamping every existing row with the migration's own run time would be exactly the invented
        // fact `CLAUDE.md` forbids, and "null" already means the true thing for a site nothing has
        // written a signal for yet ("not recorded"), which `SiteInstallationSignals.None` reads back
        // as.
        builder.Property<DateTimeOffset?>("FirstSeenAt").HasColumnName("first_seen_at");
        builder.Property<DateTimeOffset?>("LastSeenAt").HasColumnName("last_seen_at");
        builder.Property<string?>("LastRefusedOrigin").HasColumnName("last_refused_origin");
        builder.Property<DateTimeOffset?>("LastRefusedOriginAt").HasColumnName("last_refused_origin_at");
    }
}
