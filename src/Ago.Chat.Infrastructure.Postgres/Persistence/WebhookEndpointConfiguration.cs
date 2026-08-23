using Ago.Chat.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Ago.Chat.Infrastructure.Postgres.Persistence;

internal sealed class WebhookEndpointConfiguration : IEntityTypeConfiguration<WebhookEndpoint>
{
    public void Configure(EntityTypeBuilder<WebhookEndpoint> builder)
    {
        builder.ToTable("webhook_endpoints");
        builder.HasKey(e => e.Id);
        builder.Property(e => e.Id).HasColumnName("id").HasConversion(IdConverters.WebhookEndpoint).ValueGeneratedNever();
        builder.Property(e => e.SiteId).HasColumnName("site_id").HasConversion(IdConverters.Site);
        builder.Property(e => e.Url).HasColumnName("url")
            .HasConversion(url => url.ToString(), value => new Uri(value))
            .IsRequired();
        // `secret_ciphertext`, not `secret_hash` - this item's own ADR reasons through why a
        // reversible cipher, not a one-way hash, is what this column has to be (WebhookEndpoint's own
        // remarks): the dispatcher must reproduce the exact secret to sign every future delivery, a
        // hash cannot support that. A deliberate departure from the backlog item's literal column
        // name, made explicit here rather than silently naming a ciphertext column "hash".
        builder.Property(e => e.SecretCiphertext).HasColumnName("secret_ciphertext").IsRequired();
        builder.Property(e => e.Active).HasColumnName("active");
        builder.Property(e => e.CreatedAt).HasColumnName("created_at");

        builder.HasOne<Site>().WithMany().HasForeignKey(e => e.SiteId);

        // Every list this item ships (ListWebhookEndpointsHandler) filters by site alone and is small
        // (an operator-managed set, not an ever-growing log) - one index on the FK column serves it,
        // the same "one index per real query path" discipline as AttachmentConfiguration's own remarks.
        builder.HasIndex(e => e.SiteId).HasDatabaseName("ix_webhook_endpoints_site_id");
    }
}
