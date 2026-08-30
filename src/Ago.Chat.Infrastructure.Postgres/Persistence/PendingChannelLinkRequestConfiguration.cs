using Ago.Chat.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Ago.Chat.Infrastructure.Postgres.Persistence;

internal sealed class PendingChannelLinkRequestConfiguration : IEntityTypeConfiguration<PendingChannelLinkRequest>
{
    public void Configure(EntityTypeBuilder<PendingChannelLinkRequest> builder)
    {
        builder.ToTable("pending_channel_link_requests");
        builder.HasKey(p => p.Id);
        builder.Property(p => p.Id)
            .HasColumnName("id").HasConversion(IdConverters.PendingChannelLinkRequest).ValueGeneratedNever();
        builder.Property(p => p.SiteId).HasColumnName("site_id").HasConversion(IdConverters.Site);
        builder.Property(p => p.VisitorId).HasColumnName("visitor_id").HasConversion(IdConverters.Visitor);

        // Stored as the CLR member name - ChannelIdentityConfiguration's own precedent for this enum.
        builder.Property(p => p.Kind).HasColumnName("kind").HasConversion<string>().HasMaxLength(32);

        builder.Property(p => p.CodeHash).HasColumnName("code_hash").IsRequired();
        builder.Property(p => p.RequestedByOperatorId)
            .HasColumnName("requested_by_operator_id").HasConversion(IdConverters.NullableOperator);
        builder.Property(p => p.CreatedAt).HasColumnName("created_at");
        builder.Property(p => p.ExpiresAt).HasColumnName("expires_at");
        builder.Property(p => p.ConsumedAt).HasColumnName("consumed_at");

        builder.HasOne<Site>().WithMany().HasForeignKey(p => p.SiteId);
        builder.HasOne<Visitor>().WithMany().HasForeignKey(p => p.VisitorId);

        // The lookup key IPendingChannelLinkRequestRepository.FindLiveAsync asks on - deliberately not
        // unique (PendingChannelLinkRequest's own remarks on why a code, unlike an OperatorInvite's, is
        // never a global bearer credential and can legitimately collide across sites or across two
        // requests on the same site). code_hash is not included alone in any index: every real lookup
        // scopes by (site, kind) first, which this composite index already leads with.
        builder.HasIndex(p => new { p.SiteId, p.Kind, p.CodeHash })
            .HasDatabaseName("ix_pending_channel_link_requests_site_kind_code_hash");
    }
}
