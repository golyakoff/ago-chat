using Ago.Chat.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Ago.Chat.Infrastructure.Postgres.Persistence;

/// <summary>
/// `23-33`: `team_message_removals` - one row per removal, written inside the same
/// `SaveChangesAsync` that tombstones the `team_messages` row it names
/// (`TeamChatRepository.RemoveAsync`). The backlog item's own words for this table are "its own small
/// table, no aggregate" - the same physical shape `adr/0118`'s `module_revoke_overrides` takes for an
/// unrelated exceptional-act record - but this one's foreign keys are not a restatement of that
/// item's; they are the opposite call, made deliberately rather than copied.
///
/// <para><b>A real FK to <c>sites</c> and to <c>team_messages</c>, both `ON DELETE CASCADE` -
/// diverging from `module_revoke_overrides`' own no-FK choice.</b> That table's own remarks state
/// why it must survive the tenant's account closing: the record exists to answer "who took this away
/// from me" for a tenant that might ask *after* losing the ability to look, which a cascading FK would
/// silently defeat. This table answers a different question, asked by a different party - an internal
/// moderation log, visible only to the tenant's own team, about content that belongs entirely to that
/// team's own room. `personal-data.md`'s own `team_messages` row already commits to "the whole room
/// goes, or none of it does" when a site is erased (`TeamMessageConfiguration`'s own cascade) - a
/// removal record that survived that erasure would be the one piece of the room left standing, and
/// would itself become an unreachable fragment of personal data (who removed what) with nothing left
/// to attach it to. Cascading it away with the room it describes is the correct reading of `23-32`'s
/// own Done-when ("erasing the site erases the room"), not an oversight carried over from a different
/// table's reasoning.</para>
///
/// <para><b>Within ordinary operation - the tenant still exists, one message among many was removed -
/// this record does survive the message it describes</b>, exactly as the backlog item's own Done-when
/// asks: `TeamChatRepository`'s tombstone design (`Ago.Chat.Domain.TeamMessage.RemovedAt`) means the
/// `team_messages` row itself is never deleted independently of the site, only marked, so this FK
/// never actually fires except alongside that same site-wide cascade - the row genuinely outlives the
/// message's own *content*, which is what "removed" means to every operator who was not the one who
/// removed it.</para>
/// </summary>
internal sealed class TeamMessageRemovalEntityConfiguration : IEntityTypeConfiguration<TeamMessageRemovalEntity>
{
    public void Configure(EntityTypeBuilder<TeamMessageRemovalEntity> builder)
    {
        builder.ToTable("team_message_removals");
        builder.HasKey(e => e.Id);
        builder.Property(e => e.Id).HasColumnName("id").ValueGeneratedNever();
        builder.Property(e => e.TeamMessageId).HasColumnName("team_message_id").HasConversion(IdConverters.TeamMessage).IsRequired();
        builder.Property(e => e.SiteId).HasColumnName("site_id").HasConversion(IdConverters.Site).IsRequired();
        builder.Property(e => e.RemovedByOperatorId).HasColumnName("removed_by_operator_id").HasConversion(IdConverters.Operator).IsRequired();
        builder.Property(e => e.RemovedAt).HasColumnName("removed_at").IsRequired();

        builder.HasOne<Site>().WithMany().HasForeignKey(e => e.SiteId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<TeamMessage>().WithMany().HasForeignKey(e => e.TeamMessageId).OnDelete(DeleteBehavior.Cascade);
        // The remover's own row is never physically deleted (RemoveOperatorHandler soft-deletes via
        // `removed_at` on `operators`, never a row DELETE) - the same integrity fact
        // TeamMessageConfiguration's own FK on author_operator_id already states for the identical
        // reason.
        builder.HasOne<Operator>().WithMany().HasForeignKey(e => e.RemovedByOperatorId).OnDelete(DeleteBehavior.Cascade);

        // One removal per message - RemoveTeamMessageHandler's own idempotency check keeps a retry
        // from ever trying to insert a second one, but the constraint is real defense in depth
        // (db-migration.md: "the invariant is real application logic plus a real constraint"), the
        // same belt-and-braces posture ix_team_messages_site_sequence's own remarks take.
        builder.HasIndex(e => e.TeamMessageId).IsUnique().HasDatabaseName("ix_team_message_removals_team_message_id");

        // The one site-scoped read this item does not build a caller for yet (no console screen -
        // this item's own scope has no "view the removal log" UI), but every table in this codebase
        // carries an index for its own site-scoped read regardless (db-migration skill: "multi-tenancy
        // is not optional"), the same "future caller, no migration of its own" reasoning
        // ModuleRevokeOverrideEntityConfiguration's own remarks give for its identical index.
        builder.HasIndex(e => e.SiteId).HasDatabaseName("ix_team_message_removals_site_id");
    }
}
