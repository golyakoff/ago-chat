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
        // `25-43`: the price version this subscription was actually last charged under for each of
        // the two seat-pricing keys - 0 for an option row (BillingSubscription's own remarks on the
        // "meaningless for an option row" convention shared with RequestedSeats/Tier above).
        builder.Property(s => s.BaseSeatPriceVersion).HasColumnName("base_seat_price_version");
        builder.Property(s => s.ExtraSeatPriceVersion).HasColumnName("extra_seat_price_version");
        // `25-41`: the identical shape - a purchasable count and the price version it was last
        // charged under, meaningless (left at `0`) for an option row, the same convention
        // RequestedSeats/BaseSeatPriceVersion above already establish.
        builder.Property(s => s.ExtraAdministratorsPurchased).HasColumnName("extra_administrators_purchased");
        builder.Property(s => s.AdminExtraPriceVersion).HasColumnName("admin_extra_price_version");
        builder.Property(s => s.Status).HasColumnName("status").HasConversion<string>();
        builder.Property(s => s.PaymentMethodId).HasColumnName("payment_method_id");
        builder.Property(s => s.CreatedAt).HasColumnName("created_at");

        // `13-03`: the recurring-charge job's own shape - see each property's own remarks on
        // BillingSubscription for what each column means and who writes it.
        builder.Property(s => s.CurrentPeriodEnd).HasColumnName("current_period_end");
        builder.Property(s => s.PastDueSince).HasColumnName("past_due_since");
        builder.Property(s => s.LastRenewalAttemptAt).HasColumnName("last_renewal_attempt_at");
        builder.Property(s => s.CancelRequested).HasColumnName("cancel_requested").HasDefaultValue(false);
        builder.Property(s => s.PendingSeatCount).HasColumnName("pending_seat_count");
        builder.Property(s => s.PendingTier).HasColumnName("pending_tier");

        // `23-86`/`adr/0159`: null for the account's own base row, a real value naming the purchased
        // option for every other row - see BillingSubscription.OptionKey's own remarks.
        builder.Property(s => s.OptionKey).HasColumnName("option_key")
            .HasMaxLength(BillingOptionKey.MaxLength).HasConversion(IdConverters.NullableBillingOptionKey);

        builder.HasOne<Site>().WithMany().HasForeignKey(s => s.SiteId);

        // Serves BillingWebhookApplier's own lookup: WHERE site_id = @x, most-recent-first - the same
        // "index the column a real caller filters by" shape ix_webhook_deliveries_endpoint_id_id
        // establishes for its own repository.
        builder.HasIndex(s => new { s.SiteId, s.CreatedAt }).HasDatabaseName("ix_billing_subscriptions_site_id_created_at");

        // `13-03`: ListDueForRenewalAsync's own candidate query filters on (status, current_period_end)
        // for a Succeeded row and (status, last_renewal_attempt_at) for a PastDue one - a partial index
        // on status alone would still force a second-column scan for either branch, so this indexes the
        // pair the recurring-charge job's own WHERE clause actually reads together, the same "index the
        // column a real caller filters by" reasoning ix_billing_subscriptions_site_id_created_at already
        // gives just above.
        builder.HasIndex(s => new { s.Status, s.CurrentPeriodEnd }).HasDatabaseName("ix_billing_subscriptions_status_current_period_end");
    }
}
