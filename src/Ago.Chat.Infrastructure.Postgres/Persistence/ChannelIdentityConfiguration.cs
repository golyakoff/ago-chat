using Ago.Chat.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Ago.Chat.Infrastructure.Postgres.Persistence;

internal sealed class ChannelIdentityConfiguration : IEntityTypeConfiguration<ChannelIdentity>
{
    public void Configure(EntityTypeBuilder<ChannelIdentity> builder)
    {
        builder.ToTable("channel_identities");
        builder.HasKey(c => c.Id);
        builder.Property(c => c.Id)
            .HasColumnName("id").HasConversion(IdConverters.ChannelIdentity).ValueGeneratedNever();
        builder.Property(c => c.SiteId).HasColumnName("site_id").HasConversion(IdConverters.Site);
        builder.Property(c => c.VisitorId).HasColumnName("visitor_id").HasConversion(IdConverters.Visitor);

        // Stored as the CLR member name, the default HasConversion<string>() shape ConversationState
        // and AttachmentState already use - deliberately not PositionConverter's hand-written
        // kebab-case mapping, which exists there only because a CHECK constraint written in `11-01`
        // fixed those two literals. Nothing constrains these, so the plain default is honest.
        builder.Property(c => c.Kind).HasColumnName("kind").HasConversion<string>().HasMaxLength(32);

        builder.Property(c => c.Address)
            .HasColumnName("external_address")
            .HasMaxLength(ExternalChannelAddress.MaxLength)
            .HasConversion(ExternalChannelAddressConverter.Instance);

        builder.Property(c => c.FirstSeenAt).HasColumnName("first_seen_at");
        builder.Property(c => c.LastSeenAt).HasColumnName("last_seen_at");

        // `14-12`: ChannelCredential.Active/ChannelCredentialConfiguration's own precedent, mirrored
        // exactly - see ChannelIdentity's own remarks.
        builder.Property(c => c.Active).HasColumnName("active");
        builder.Property(c => c.UnlinkedAt).HasColumnName("unlinked_at");

        // The lookup key IChannelIdentityRepository.FindAsync asks on, and the storage-level backstop
        // for "one external address is one visitor per site per channel" - the same "the index is the
        // backstop, not the primary mechanism" division adr/0019 draws for messages. Two processes
        // racing the very first inbound message from the same number cannot both create an identity;
        // one insert is refused, and its retry resolves the winner's row.
        //
        // `14-12`: now a *partial* unique index (Active only), not a plain one -
        // ux_channel_credentials_site_kind_active's own precedent, for the identical reason: an unlinked
        // identity must never block a fresh link of the same external address later (ChannelIdentity's
        // own remarks on why the unique index had to move for this item to be representable at all - a
        // plain unique index would make the second Link's INSERT fail against the first, now-inactive
        // row still occupying the same key).
        builder.HasIndex(c => new { c.SiteId, c.Kind, c.Address })
            .IsUnique()
            .HasFilter("active")
            .HasDatabaseName("ux_channel_identities_site_kind_address_active");

        // `18-07`: `AutoCloseInactiveConversationsQuery`'s own remarks (`18-06`) claimed there was no
        // index on `channel_identities.visitor_id`. Checked while wiring this item's own gating call
        // to `FindMostRecentForVisitorAsync` and found that claim wrong - EF's own convention already
        // creates one for the `HasOne&lt;Visitor&gt;` foreign key below, it was just never spelled out
        // here the way this table's unique index is, so a reading of this file (rather than the live
        // schema) would miss it, which is what happened. Naming it explicitly, matching every other
        // index in this file, rather than leaving it as an EF-generated `IX_...` default - a
        // documentation fix, not a new index; the generated migration is a rename, not a create.
        builder.HasIndex(c => c.VisitorId)
            .HasDatabaseName("ix_channel_identities_visitor_id");

        // Not navigations (aggregates stay independent - data-model.md lists site_id as a plain
        // foreign key, never a loaded Site) - HasOne/WithMany with no exposed property is how EF adds
        // the DB-level constraint without adding a Domain reference, exactly as VisitorConfiguration
        // already does for its own site_id.
        builder.HasOne<Site>().WithMany().HasForeignKey(c => c.SiteId);
        builder.HasOne<Visitor>().WithMany().HasForeignKey(c => c.VisitorId);
    }
}
