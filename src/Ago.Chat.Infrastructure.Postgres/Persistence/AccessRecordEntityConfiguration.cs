using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Ago.Chat.Infrastructure.Postgres.Persistence;

/// <summary>
/// `24-12`: `access_records` - one row per boundary-crossing read, deliberately holding nothing about
/// what was read. Every column is checked against that question, the same discipline
/// <see cref="ErasureRecordEntityConfiguration"/>'s own remarks apply to <c>erasure_records</c>:
/// <list type="bullet">
/// <item><see cref="AccessRecordEntity.SiteId"/> names the tenant whose data was read - metadata, not
/// content, the same distinction <c>personal-data.md</c> already draws for <c>sites.name</c>. No FK -
/// see <see cref="AccessRecordEntity"/>'s own remarks.</item>
/// <item><see cref="AccessRecordEntity.ActorId"/> names who read it - an <c>OperatorId</c> or a
/// Keycloak `sub`, never a copy of anything the actor read.</item>
/// <item><see cref="AccessRecordEntity.ResourceKind"/>/<see cref="AccessRecordEntity.ResourceId"/>
/// name *which* row was reached - a conversation id, a channel-identity id, an enabled-module id -
/// never that row's own content.</item>
/// </list>
/// <see cref="Ago.Chat.Integration.Tests"/>'s own access-record shape test asserts this positively,
/// over a real persisted row, the same "a column added later without updating this note would pass a
/// shape-based test and still leak" reasoning <see cref="ErasureRecordEntityConfiguration"/>'s own
/// remarks give for its own equivalent test.
/// </summary>
internal sealed class AccessRecordEntityConfiguration : IEntityTypeConfiguration<AccessRecordEntity>
{
    public void Configure(EntityTypeBuilder<AccessRecordEntity> builder)
    {
        builder.ToTable("access_records");
        builder.HasKey(e => e.Id);
        builder.Property(e => e.Id).HasColumnName("id").ValueGeneratedNever();
        builder.Property(e => e.OccurredAt).HasColumnName("occurred_at").IsRequired();
        builder.Property(e => e.AccessKind).HasColumnName("access_kind").IsRequired();
        // No HasOne<Site>()/HasForeignKey - see AccessRecordEntity's own remarks for why the absence
        // is deliberate, not a gap.
        builder.Property(e => e.SiteId).HasColumnName("site_id").HasConversion(IdConverters.NullableSite);
        builder.Property(e => e.ActorKind).HasColumnName("actor_kind").IsRequired();
        builder.Property(e => e.ActorId).HasColumnName("actor_id").IsRequired();
        builder.Property(e => e.ResourceKind).HasColumnName("resource_kind");
        builder.Property(e => e.ResourceId).HasColumnName("resource_id");

        // `ck_access_records_*`: the same "a CHECK constraint backstops the enum at the storage level"
        // reasoning ErasureRecordEntityConfiguration's own remarks give for erasure_records - a stray
        // value written by a future direct SQL statement is rejected by Postgres, not merely by C#
        // code nobody ran.
        builder.ToTable(t =>
        {
            t.HasCheckConstraint(
                "ck_access_records_access_kind",
                "access_kind IN ('CrossConversationHistoryRead', 'OwnerSiteList', 'OwnerSiteDetail', "
                + "'OwnerModuleGrant', 'OwnerModuleRevoke', 'OwnerChannelIdentityUnlink')");
            t.HasCheckConstraint("ck_access_records_actor_kind", "actor_kind IN ('Operator', 'PlatformOwner')");
            t.HasCheckConstraint(
                "ck_access_records_resource_kind",
                "resource_kind IS NULL OR resource_kind IN ('Conversation', 'ChannelIdentity', 'EnabledModule')");
        });

        // Serves IAccessRecordRepository.ListForSiteAsync's own keyset read - a tenant's own page,
        // newest first, is the one query this table serves besides the insert. `id` is included in the
        // index (not just `site_id`) because the query orders and pages by `id desc` within one site -
        // the same "index the columns the WHERE and ORDER BY actually use together" reasoning every
        // other keyset index in this codebase follows (data-model.md bans OFFSET, so a keyset read's
        // own index shape is load-bearing, not incidental).
        builder.HasIndex(e => new { e.SiteId, e.Id })
            .HasDatabaseName("ix_access_records_site_id_id");
    }
}
