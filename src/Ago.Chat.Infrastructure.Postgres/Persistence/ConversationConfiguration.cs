using Ago.Chat.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Ago.Chat.Infrastructure.Postgres.Persistence;

internal sealed class ConversationConfiguration : IEntityTypeConfiguration<Conversation>
{
    public void Configure(EntityTypeBuilder<Conversation> builder)
    {
        builder.ToTable("conversations");
        builder.HasKey(c => c.Id);
        builder.Property(c => c.Id).HasColumnName("id").HasConversion(IdConverters.Conversation).ValueGeneratedNever();
        builder.Property(c => c.SiteId).HasColumnName("site_id").HasConversion(IdConverters.Site);
        builder.Property(c => c.VisitorId).HasColumnName("visitor_id").HasConversion(IdConverters.Visitor);
        builder.Property(c => c.OperatorId).HasColumnName("operator_id").HasConversion(IdConverters.NullableOperator);
        builder.Property(c => c.State).HasColumnName("state").HasConversion<string>();
        builder.Property(c => c.LastSequence).HasColumnName("last_sequence");
        builder.Property(c => c.CreatedAt).HasColumnName("created_at");
        builder.Property(c => c.VisitorUnreadCount).HasColumnName("visitor_unread_count");
        builder.Property(c => c.OperatorUnreadCount).HasColumnName("operator_unread_count");

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
    }
}
