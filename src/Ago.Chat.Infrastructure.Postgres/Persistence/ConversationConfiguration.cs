using Ago.Chat.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Ago.Chat.Infrastructure.Postgres.Persistence;

internal sealed class ConversationConfiguration : IEntityTypeConfiguration<Conversation>
{
    public void Configure(EntityTypeBuilder<Conversation> builder)
    {
        // `18-10`: the CHECK constraint backstops ConversationOutcome's own closed-vocabulary design at
        // the storage level - the same "anything enforcing a guarantee gets a constraint, not just
        // application code" reasoning SiteConfiguration's own widget_position/widget_locale constraints
        // already state, applied here since ConversationOutcome is a small, deliberately closed enum
        // for the identical reason those two are.
        builder.ToTable("conversations", t =>
        {
            t.HasCheckConstraint(
                "ck_conversations_outcome", "outcome IN ('Unset', 'Converted', 'NotConverted', 'FollowUpNeeded')");
        });
        builder.HasKey(c => c.Id);
        builder.Property(c => c.Id).HasColumnName("id").HasConversion(IdConverters.Conversation).ValueGeneratedNever();
        builder.Property(c => c.SiteId).HasColumnName("site_id").HasConversion(IdConverters.Site);
        builder.Property(c => c.VisitorId).HasColumnName("visitor_id").HasConversion(IdConverters.Visitor);
        builder.Property(c => c.OperatorId).HasColumnName("operator_id").HasConversion(IdConverters.NullableOperator);
        builder.Property(c => c.State).HasColumnName("state").HasConversion<string>();
        builder.Property(c => c.LastSequence).HasColumnName("last_sequence");
        builder.Property(c => c.CreatedAt).HasColumnName("created_at");

        // `18-07`: nullable - null for every conversation that predates this column, and for every
        // conversation still open. See Conversation.ClosedAt's own remarks.
        builder.Property(c => c.ClosedAt).HasColumnName("closed_at");

        // `18-10`: non-nullable with a database default, not nullable-with-null-meaning-Unset the way
        // ClosedAt is nullable-with-null-meaning-"still open" right above it - Unset is a real,
        // queryable member of the enum (Conversation.Outcome's own remarks: "the default... until an
        // operator explicitly changes it"), not the absence of a row's opinion the way a null ClosedAt
        // is. The default backfills every row written before this migration - the demo tenants included -
        // to the same honest "nobody has recorded one" value new rows start at, with no separate backfill
        // migration pretending historical conversations were ever asked.
        builder.Property(c => c.Outcome).HasColumnName("outcome").HasConversion<string>()
            .IsRequired().HasDefaultValue(ConversationOutcome.Unset);

        builder.Property(c => c.VisitorUnreadCount).HasColumnName("visitor_unread_count");
        builder.Property(c => c.OperatorUnreadCount).HasColumnName("operator_unread_count");

        // `5-15`: no index. This column is only ever read as part of the aggregate the write path has
        // already located by primary key, never filtered or ordered on - data-model.md's rule is that
        // an index arrives with its first real reader, and this one has none.
        builder.Property(c => c.OperatorLastReadSequence).HasColumnName("operator_last_read_sequence");

        // `6-09`: no index either, for the same reason - it is read as part of an aggregate already
        // located by primary key (CloseConversationHandler) or already materialised by
        // GetAssignedToOperatorAsync (OperatorConversationReleaser), never filtered on. The one query
        // that *does* filter on it is this item's own migration backfill, which runs once.
        //
        // An ordinary mapped property rather than a shadow property, unlike operators.active_chats
        // right next door: that column has a raw-SQL writer (IOperatorCapacity's atomic
        // compare-and-set) an EF load-mutate-save could race, and this one does not - the Conversation
        // aggregate is its only writer, saved under this row's own `xmin`. See
        // Conversation.HoldsCapacityClaim's own remarks.
        builder.Property(c => c.HoldsCapacityClaim).HasColumnName("holds_capacity_claim");

        builder.HasOne<Site>().WithMany().HasForeignKey(c => c.SiteId);
        builder.HasOne<Visitor>().WithMany().HasForeignKey(c => c.VisitorId);
        builder.HasOne<Operator>().WithMany().HasForeignKey(c => c.OperatorId);

        // Postgres's built-in system column, not an extra one we maintain ourselves - EF bumps and
        // checks it automatically on every UPDATE, which is exactly optimistic concurrency
        // (data-model.md's "version") without a migration-visible column to keep in sync by hand.
        builder.Property<uint>("xmin").IsRowVersion();

        // Messages is a computed IReadOnlyList<Message> reading the same _messages field - without
        // this Ignore, EF's own convention claims _messages as that property's backing field too,
        // colliding with the explicit field-targeted navigation below (found by running this: EF
        // reported the field "already used by Conversation.Messages").
        builder.Ignore(c => c.Messages);

        // Never a settable collection (clean-architecture.md: no public setters) - EF is pointed at
        // the private backing field directly for both reads and materialization, so the aggregate
        // loads without going through AddVisitorMessage/AddOperatorMessage.
        builder.HasMany<Message>("_messages")
            .WithOne()
            .HasForeignKey(m => m.ConversationId)
            .OnDelete(DeleteBehavior.Cascade);
        builder.Navigation("_messages").UsePropertyAccessMode(PropertyAccessMode.Field);

        // `20-07`: the identical shape, one release later - ActiveModuleTask is computed from the same
        // private-field navigation _moduleTasks reads, per ModuleTask.cs's own remarks on why this
        // aggregate is the only place a module task is constructed or mutated.
        builder.Ignore(c => c.ActiveModuleTask);
        builder.HasMany<ModuleTask>("_moduleTasks")
            .WithOne()
            .HasForeignKey(t => t.ConversationId)
            .OnDelete(DeleteBehavior.Cascade);
        builder.Navigation("_moduleTasks").UsePropertyAccessMode(PropertyAccessMode.Field);

        // In-memory-only facts (1-01) - nothing publishes them yet (outbox is Stage 2), so there is
        // nothing here for EF to persist.
        builder.Ignore(c => c.DomainEvents);

        // 4-01: data-model.md named this index from the start ("Keys and indexes") but nothing had
        // actually created it until WaitingConversationClaimQuery gave it a real reader - without it,
        // 4-02's SKIP LOCKED claim is a full-table scan under lock, which defeats the whole point of
        // letting multiple Worker replicas claim in parallel.
        builder.HasIndex(c => c.SiteId)
            .HasDatabaseName("ix_conversations_waiting")
            .HasFilter("state = 'Waiting'");

        // `5-08`: the admin/supervisor site-wide conversation list has no state filter (unlike
        // ix_conversations_waiting above), so that partial index cannot serve it -
        // ConversationReadStore.GetAllForSiteAsync's own keyset (id descending) needs an index
        // covering both the site_id filter and the id ordering, or it is a full-table scan sorted in
        // memory the moment a site accumulates more than a handful of conversations.
        builder.HasIndex(c => new { c.SiteId, c.Id })
            .HasDatabaseName("ix_conversations_site_all");

        // `16-02`: the identical shadow-property shape as SiteConfiguration's own
        // "ErasureRequestedAt" - see its remarks for the full reasoning. One extra reason it matters
        // more here: Conversation's own repository (ConversationRepository.GetByIdAsync) loads the
        // entire aggregate, messages included (`Include("_messages")`), so routing an erase *request*
        // through the aggregate would both load a conversation's full message history just to flip one
        // flag and race this row's `xmin` against every ordinary message send - exactly the failure
        // mode this shadow-property/raw-SQL split is chosen to avoid.
        builder.Property<DateTimeOffset?>("ErasureRequestedAt").HasColumnName("erasure_requested_at");
        builder.HasIndex("ErasureRequestedAt")
            .HasDatabaseName("ix_conversations_erasure_pending")
            .HasFilter("erasure_requested_at is not null");

        // `18-07`: EF's own convention had already created an unnamed single-column index on
        // VisitorId here (for the HasOne&lt;Visitor&gt; foreign key below) - not a real gap, just
        // never spelled out in this file the way every other index on this table is, so a
        // code-reading audit (rather than a live `\d conversations`) would miss it. This item's own
        // read (ConversationReadStore.GetVisitorHistoryAsync) is a keyset scan - filtered by
        // visitor_id *and* ordered by id - that the single-column index cannot serve without a
        // separate sort, so the composite below replaces it outright (the generated migration drops
        // the old one) rather than sitting alongside it as a second index EF would otherwise keep
        // paying to maintain on every insert.
        builder.HasIndex(c => new { c.VisitorId, c.Id })
            .HasDatabaseName("ix_conversations_visitor_all");

        // `18-12`: the identical "computed property, EF pointed at the private fields by name" shape
        // MessageConfiguration already uses for Message.Content's own three backing fields - see that
        // class's remarks. All four nullable, all unindexed: the report reads them through
        // OperatorAnalyticsReadStore's own GROUPING SETS over the whole table (bounded by the same
        // site_id/created_at predicate every other analytics query there already uses), never by a
        // point lookup on any one of these columns.
        builder.Ignore(c => c.Source);
        builder.Property<string?>("_trafficReferrerHost")
            .HasColumnName("traffic_referrer_host").HasMaxLength(TrafficSource.MaxLength);
        builder.Property<string?>("_trafficUtmSource")
            .HasColumnName("traffic_utm_source").HasMaxLength(TrafficSource.MaxLength);
        builder.Property<string?>("_trafficUtmMedium")
            .HasColumnName("traffic_utm_medium").HasMaxLength(TrafficSource.MaxLength);
        builder.Property<string?>("_trafficUtmCampaign")
            .HasColumnName("traffic_utm_campaign").HasMaxLength(TrafficSource.MaxLength);
    }
}
