using Ago.Chat.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Ago.Chat.Infrastructure.Postgres.Persistence;

/// <summary>`25-84`: <see cref="DownloadOverageCharge"/>'s own mapping - the identical shape
/// <see cref="BillingSubscriptionConfiguration"/> uses for the analogous "pending row a webhook
/// promotes" aggregate, including storing both enums as their member names rather than ordinals so a
/// person reading the table with `psql` sees `Succeeded`, not `1`.</summary>
internal sealed class DownloadOverageChargeConfiguration : IEntityTypeConfiguration<DownloadOverageCharge>
{
    public void Configure(EntityTypeBuilder<DownloadOverageCharge> builder)
    {
        builder.ToTable("download_overage_charges");
        builder.HasKey(c => c.Id);
        builder.Property(c => c.Id).HasColumnName("id").HasConversion(IdConverters.DownloadOverageCharge).ValueGeneratedNever();
        builder.Property(c => c.SiteId).HasColumnName("site_id").HasConversion(IdConverters.Site);
        builder.Property(c => c.PeriodMonth).HasColumnName("period_month");
        builder.Property(c => c.Source).HasColumnName("source").HasConversion<string>();
        builder.Property(c => c.Status).HasColumnName("status").HasConversion<string>();
        builder.Property(c => c.BytesOver).HasColumnName("bytes_over");
        builder.Property(c => c.AmountRub).HasColumnName("amount_rub").HasPrecision(10, 2);
        builder.Property(c => c.PriceVersion).HasColumnName("price_version");
        builder.Property(c => c.YooKassaPaymentId).HasColumnName("yookassa_payment_id");
        builder.Property(c => c.CreatedAt).HasColumnName("created_at");
        builder.Property(c => c.SettledAt).HasColumnName("settled_at");

        builder.HasOne<Site>().WithMany().HasForeignKey(c => c.SiteId);

        // The read store's own only two queries - both keyed on (site, month). One index serves both.
        builder.HasIndex(c => new { c.SiteId, c.PeriodMonth })
            .HasDatabaseName("ix_download_overage_charges_site_id_period_month");

        // `BillingWebhookApplier` finds a checkout row by the payment id ЮKassa quotes back. Unique so
        // two rows can never claim the same payment - the same guarantee
        // `billing_subscriptions.yookassa_payment_id` relies on, made explicit here because this column
        // is nullable (an Invoice row has no payment of its own) and a plain unique index over a
        // nullable column in Postgres already permits any number of NULLs.
        builder.HasIndex(c => c.YooKassaPaymentId)
            .IsUnique()
            .HasDatabaseName("ux_download_overage_charges_yookassa_payment_id");
    }
}
