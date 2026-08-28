using Ago.Chat.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Ago.Chat.Infrastructure.Postgres.Persistence;

internal sealed class BillingWebhookEventConfiguration : IEntityTypeConfiguration<BillingWebhookEvent>
{
    public void Configure(EntityTypeBuilder<BillingWebhookEvent> builder)
    {
        builder.ToTable("billing_webhook_events");
        builder.HasKey(e => e.Id);
        builder.Property(e => e.Id).HasColumnName("id").HasConversion(IdConverters.BillingWebhookEvent).ValueGeneratedNever();
        builder.Property(e => e.YooKassaPaymentId).HasColumnName("yookassa_payment_id").IsRequired();
        builder.Property(e => e.EventType).HasColumnName("event_type").IsRequired();
        builder.Property(e => e.ReceivedAt).HasColumnName("received_at");

        // `13-02`: the idempotency ledger itself - "a redelivered payment.succeeded must not double-apply"
        // is this UNIQUE constraint, enforced by Postgres, not an application-level check alone.
        // BillingWebhookApplier catches the resulting unique-violation and treats it as
        // "already recorded, no-op" - the same catch-the-violation shape WebhookDeliveryRepository.SaveAsync
        // already uses for its own (endpoint_id, message_id) ledger.
        builder.HasIndex(e => new { e.YooKassaPaymentId, e.EventType })
            .IsUnique()
            .HasDatabaseName("ux_billing_webhook_events_payment_id_event_type");
    }
}
