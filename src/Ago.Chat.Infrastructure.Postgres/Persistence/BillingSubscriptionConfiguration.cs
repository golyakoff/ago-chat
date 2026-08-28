using Ago.Chat.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Ago.Chat.Infrastructure.Postgres.Persistence;

internal sealed class BillingSubscriptionConfiguration : IEntityTypeConfiguration<BillingSubscription>
{
    public void Configure(EntityTypeBuilder<BillingSubscription> builder)
    {
        builder.ToTable("billing_subscriptions");
        builder.HasKey(s => s.Id);
        builder.Property(s => s.Id).HasColumnName("id").HasConversion(IdConverters.BillingSubscription).ValueGeneratedNever();
        builder.Property(s => s.SiteId).HasColumnName("site_id").HasConversion(IdConverters.Site);
        // `13-02`: the natural key BillingWebhookApplier looks a pending row up by - unique because
        // exactly one checkout attempt ever creates a given ЮKassa payment id
        // (CreateCheckoutSessionHandler's own single INSERT per successful CreatePaymentAsync call).
        builder.Property(s => s.YooKassaPaymentId).HasColumnName("yookassa_payment_id").IsRequired();
        builder.HasIndex(s => s.YooKassaPaymentId).IsUnique().HasDatabaseName("ux_billing_subscriptions_yookassa_payment_id");
        builder.Property(s => s.RequestedSeats).HasColumnName("requested_seats");
        builder.Property(s => s.Tier).HasColumnName("tier").IsRequired();
        builder.Property(s => s.Status).HasColumnName("status").HasConversion<string>();
        builder.Property(s => s.PaymentMethodId).HasColumnName("payment_method_id");
        builder.Property(s => s.CreatedAt).HasColumnName("created_at");

        builder.HasOne<Site>().WithMany().HasForeignKey(s => s.SiteId);

        // Serves BillingWebhookApplier's own lookup: WHERE site_id = @x, most-recent-first - the same
        // "index the column a real caller filters by" shape ix_webhook_deliveries_endpoint_id_id
        // establishes for its own repository.
        builder.HasIndex(s => new { s.SiteId, s.CreatedAt }).HasDatabaseName("ix_billing_subscriptions_site_id_created_at");
    }
}
