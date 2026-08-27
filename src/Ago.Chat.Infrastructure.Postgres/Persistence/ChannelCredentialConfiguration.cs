using Ago.Chat.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Ago.Chat.Infrastructure.Postgres.Persistence;

internal sealed class ChannelCredentialConfiguration : IEntityTypeConfiguration<ChannelCredential>
{
    public void Configure(EntityTypeBuilder<ChannelCredential> builder)
    {
        builder.ToTable("channel_credentials");
        builder.HasKey(c => c.Id);
        builder.Property(c => c.Id)
            .HasColumnName("id").HasConversion(IdConverters.ChannelCredential).ValueGeneratedNever();
        builder.Property(c => c.SiteId).HasColumnName("site_id").HasConversion(IdConverters.Site);

        // Stored as the CLR member name - ChannelIdentityConfiguration's own precedent for this enum.
        builder.Property(c => c.Kind).HasColumnName("kind").HasConversion<string>().HasMaxLength(32);

        builder.Property(c => c.TokenCiphertext).HasColumnName("token_ciphertext").IsRequired();
        builder.Property(c => c.WebhookSecretHash).HasColumnName("webhook_secret_hash").IsRequired();
        builder.Property(c => c.Active).HasColumnName("active");
        builder.Property(c => c.CreatedAt).HasColumnName("created_at");

        builder.HasOne<Site>().WithMany().HasForeignKey(c => c.SiteId);

        // `adr/0069`'s "one bot per tenant per channel" - the storage-level backstop for the check
        // RegisterChannelCredentialHandler makes before calling ChannelCredential.Register, the same
        // "index is the backstop, not the primary mechanism" division ChannelIdentityConfiguration's
        // own remarks draw. A *partial* unique index (Active only) rather than a plain one on
        // (site_id, kind): a revoked credential must never block registering a replacement, which a
        // plain unique index on the pair would do the moment the first credential's row was merely
        // deactivated rather than deleted.
        builder.HasIndex(c => new { c.SiteId, c.Kind })
            .IsUnique()
            .HasFilter("active")
            .HasDatabaseName("ux_channel_credentials_site_kind_active");
    }
}
