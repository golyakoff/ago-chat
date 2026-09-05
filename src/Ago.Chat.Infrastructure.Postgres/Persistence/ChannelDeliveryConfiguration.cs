using Ago.Chat.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Ago.Chat.Infrastructure.Postgres.Persistence;

/// <summary>
/// `23-19`: <see cref="ChannelDelivery"/>'s own table - see that type's own remarks for the full
/// address-versus-reference argument. This is the one place the two foreign-key decisions below are
/// spelled out at the schema level.
///
/// <para><b><c>site_id</c>: <c>ON DELETE CASCADE</c></b>, the same direct-to-<c>sites</c> shape
/// <c>ChannelIdentityConfiguration</c>/<c>VisitorConfiguration</c> already use. Unlike `adr/0112`'s
/// erasure receipt or `adr/0113`'s access record, this table has no reason to outlive the site it is
/// about: once a site is fully erased there is no tenant left who could read a delivery record for it,
/// and keeping the rows around would be personal-data retention with no purpose behind it -
/// `personal-data.md`'s own minimisation principle, not a new one invented here.</para>
///
/// <para><b><c>channel_identity_id</c>: also <c>ON DELETE CASCADE</c></b>, to <c>channel_identities</c>
/// directly, rather than the FK-less pattern <c>AcceptanceRecordConfiguration</c>/<c>ErasureRecordConfiguration</c>/
/// <c>AccessRecordConfiguration</c> use for their own subject columns. Those three tables need to
/// survive the erasure of the very row they are evidence about, while everything else around them
/// stays alive - that is the whole reason they give theirs up. <see cref="ChannelIdentity"/> is never
/// hard-deleted on its own (<c>Unlink</c> only flips <see cref="ChannelIdentity.Active"/>); the only
/// hard delete is <c>SiteErasureQuery.DeleteSiteAsync</c>'s cascade, which removes this table's own rows
/// in the same statement via the <c>site_id</c> cascade above. So a real, enforced foreign key here
/// never has to survive an erasure it was not designed to survive, and it buys ordinary referential
/// integrity - and a join-free path from a delivery to the identity it names - that those three
/// evidentiary tables deliberately gave up for a reason that does not apply here.</para>
///
/// <para><b><c>conversation_id</c>: no foreign key at all</b>, the one place this table does follow
/// `adr/0112`/`adr/0113`'s shape. A conversation can be erased on its own
/// (<c>ConversationErasureJob</c>), independently of the site living on, and a delivery record - which
/// carries no message content, only the outcome and the provider's own detail - is exactly the kind of
/// low-sensitivity metadata those two ADRs argue should survive the erasure of the thing it is evidence
/// about. Its own retention window (<c>ChannelDeliveryPruneJob</c>) is what eventually removes it either
/// way.</para>
/// </summary>
internal sealed class ChannelDeliveryConfiguration : IEntityTypeConfiguration<ChannelDelivery>
{
    public void Configure(EntityTypeBuilder<ChannelDelivery> builder)
    {
        builder.ToTable("channel_deliveries");
        builder.HasKey(d => d.Id);
        builder.Property(d => d.Id).HasColumnName("id").HasConversion(IdConverters.ChannelDelivery).ValueGeneratedNever();
        builder.Property(d => d.SiteId).HasColumnName("site_id").HasConversion(IdConverters.Site);
        builder.Property(d => d.ConversationId).HasColumnName("conversation_id").HasConversion(IdConverters.Conversation);
        builder.Property(d => d.MessageId).HasColumnName("message_id").HasConversion(IdConverters.Message);
        builder.Property(d => d.ChannelKind).HasColumnName("channel_kind").HasConversion<string>().HasMaxLength(32);
        builder.Property(d => d.ChannelIdentityId).HasColumnName("channel_identity_id").HasConversion(IdConverters.ChannelIdentity);
        builder.Property(d => d.Status).HasColumnName("status").HasConversion<string>().HasMaxLength(16);
        builder.Property(d => d.ProviderMessageId).HasColumnName("provider_message_id").HasMaxLength(200);
        builder.Property(d => d.FailureReason).HasColumnName("failure_reason")
            .HasMaxLength(ChannelDelivery.MaxProviderDetailLength);
        builder.Property(d => d.AttemptedAt).HasColumnName("attempted_at");

        // The idempotency ledger itself - one triggering operator message is one outbound send, so a
        // redelivered MessageAccepted's second attempt at this same insert collapses here rather than
        // growing a second row (ChannelDelivery's own remarks; ChannelDeliveryRepository catches the
        // resulting unique-violation the same way WebhookDeliveryRepository already catches its own).
        builder.HasIndex(d => d.MessageId).IsUnique().HasDatabaseName("ux_channel_deliveries_message_id");

        // Serves GetChannelDeliveriesForConversationHandler's own read: WHERE conversation_id = @x AND
        // site_id = @y, newest first - the same "equality filter plus the sort column" shape
        // ix_webhook_deliveries_endpoint_id_id already uses.
        builder.HasIndex(d => new { d.ConversationId, d.SiteId, d.AttemptedAt })
            .HasDatabaseName("ix_channel_deliveries_conversation_id_site_id_attempted_at");

        // ChannelDeliveryPruneQuery's own window scan.
        builder.HasIndex(d => d.AttemptedAt).HasDatabaseName("ix_channel_deliveries_attempted_at");

        builder.HasOne<Site>().WithMany().HasForeignKey(d => d.SiteId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<ChannelIdentity>().WithMany().HasForeignKey(d => d.ChannelIdentityId).OnDelete(DeleteBehavior.Cascade);
    }
}
