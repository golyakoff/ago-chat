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

        // `14-08`: nullable - MAX's and Telegram's own rows never populate it
        // (ChannelCredential.ProviderAccountId's own remarks). No length bound tied to VK's own group_id
        // shape (a plain positive integer as text): the same "opaque provider-owned string" treatment
        // TokenCiphertext/WebhookSecretHash already get, rather than a channel-specific constraint on a
        // channel-neutral column.
        builder.Property(c => c.ProviderAccountId).HasColumnName("provider_account_id");

        // `14-11`: nullable - every channel but Avito leaves it null
        // (ChannelCredential.RefreshTokenCiphertext's own remarks). Not IsRequired(), unlike
        // TokenCiphertext/WebhookSecretHash above: those two are populated on every single row this
        // system has ever created, this one is populated on exactly one channel's own rows.
        builder.Property(c => c.RefreshTokenCiphertext).HasColumnName("refresh_token_ciphertext");

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

        // `14-10`: the storage-level backstop for a guarantee no channel needed until WhatsApp -
        // IChannelCredentialRepository.GetActiveByProviderAccountIdAsync's own remarks explain why
        // WhatsApp's inbound routing depends on ProviderAccountId being attributable to exactly one
        // tenant (Meta's own phone_number_id is globally unique by construction, but nothing in this
        // schema enforced that before this item - a second site registering the identical id, by
        // mistake or by a provider-side reassignment this system does not control, would silently make
        // an inbound delivery route to whichever row this system's own lookup happened to find first).
        // Partial (Active only, ProviderAccountId not null) for the identical reason
        // ux_channel_credentials_site_kind_active is partial: a revoked credential must never block a
        // legitimate re-registration of the same number, and MAX's/Telegram's own rows (ProviderAccountId
        // always null) must never collide with each other under a plain unique index on a column most
        // rows leave unset.
        builder.HasIndex(c => new { c.Kind, c.ProviderAccountId })
            .IsUnique()
            .HasFilter("active AND provider_account_id IS NOT NULL")
            .HasDatabaseName("ux_channel_credentials_kind_provideraccountid_active");
    }
}
