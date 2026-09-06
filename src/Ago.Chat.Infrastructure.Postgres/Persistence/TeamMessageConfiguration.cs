using Ago.Chat.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Ago.Chat.Infrastructure.Postgres.Persistence;

/// <summary>
/// `23-32`: small and not hash-partitioned, unlike <c>messages</c> - one row per team message, across
/// every tenant that has ever used its own room, but nothing here approaches the volume
/// `Stage15RepartitionMessagesByTenantHash` exists for (adr/0087's own numbers are about visitor
/// conversation volume, which a four-operator internal room never approaches). Revisit only if a real
/// number says otherwise (CLAUDE.md: "performance claims need numbers").
/// </summary>
internal sealed class TeamMessageConfiguration : IEntityTypeConfiguration<TeamMessage>
{
    public void Configure(EntityTypeBuilder<TeamMessage> builder)
    {
        builder.ToTable("team_messages");
        builder.HasKey(m => m.Id);
        builder.Property(m => m.Id).HasColumnName("id").HasConversion(IdConverters.TeamMessage).ValueGeneratedNever();
        builder.Property(m => m.SiteId).HasColumnName("site_id").HasConversion(IdConverters.Site);
        builder.Property(m => m.AuthorOperatorId).HasColumnName("author_operator_id").HasConversion(IdConverters.Operator);
        builder.Property(m => m.AuthorIsAdmin).HasColumnName("author_is_admin");
        builder.Property(m => m.Body).HasColumnName("body").HasConversion(MessageBodyConverter.Instance);
        builder.Property(m => m.Sequence).HasColumnName("sequence");
        // `5-07`'s retry-dedup idiom, reused verbatim - see Ago.Chat.Domain.TeamMessage.ClientMessageId's
        // own remarks. Nullable for the identical reason messages.client_message_id is: a caller that
        // never sends one must not be rejected outright, only skip dedup.
        builder.Property(m => m.ClientMessageId).HasColumnName("client_message_id");
        builder.Property(m => m.CreatedAt).HasColumnName("created_at");

        // The room's own ordering key - a unique index, not only the database sequence's own
        // uniqueness, as defense in depth against a bug in the atomic UPDATE ever producing a
        // collision silently instead of a constraint violation (the same "the invariant is real
        // application logic plus a real constraint" belt-and-braces db-migration.md asks for).
        builder.HasIndex(m => new { m.SiteId, m.Sequence }).IsUnique().HasDatabaseName("ix_team_messages_site_sequence");

        // `5-07`'s retry-dedup: at most one message per (site, clientMessageId) - partial, since most
        // rows (any client that predates this convention, or simply never sent one) carry no value at
        // all and must not collide with each other, the identical shape `ix_conversation_assignments_open`
        // and `ix_sites_demo_expiry`'s own partial indexes already use for "most rows don't have this."
        builder.HasIndex(m => new { m.SiteId, m.ClientMessageId })
            .IsUnique()
            .HasDatabaseName("ix_team_messages_site_client_message_id")
            .HasFilter("client_message_id is not null");

        // Erasure: "erasing the site erases the room" (the backlog item's own Done-when) - a plain FK
        // cascade, not a bounded-batch drain job like ConversationErasureJob's own conversations/
        // messages. That job exists because those tables are large, hold visitor-facing personal data,
        // and need archive-stripping a bare cascade cannot do (SiteErasureQuery.DeleteSiteAsync's own
        // remarks); this table is small, holds no visitor data at all (personal-data.md's own new row),
        // and rides DeleteSiteAsync's existing cascade for free, the identical shape `operators`,
        // `tags` and `site_widget_activity` already take on the same table.
        builder.HasOne<Site>().WithMany().HasForeignKey(m => m.SiteId).OnDelete(DeleteBehavior.Cascade);
        // The author's own row is never physically deleted (RemoveOperatorHandler soft-deletes via
        // `removed_at`, never a row DELETE) - this FK only ever fires alongside the site's own cascade
        // above, but it is still a real integrity fact worth Postgres enforcing: a team message always
        // names an operator that (at least once) existed on this same site.
        builder.HasOne<Operator>().WithMany().HasForeignKey(m => m.AuthorOperatorId).OnDelete(DeleteBehavior.Cascade);
    }
}
