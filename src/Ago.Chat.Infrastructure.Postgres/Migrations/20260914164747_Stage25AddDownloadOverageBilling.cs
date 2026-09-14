using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ago.Chat.Infrastructure.Postgres.Migrations
{
    /// <inheritdoc />
    /// <summary>
    /// `25-84`: everything the paid escape hatch past `25-83`'s own hard download block needs -
    /// the append-only charge ledger, the per-tenant billing-mode toggle, the per-tier auto-bill cap,
    /// and the shipped 100 RUB/GB price.
    ///
    /// <para><b>Three of the four are EF-generated; two are hand-written SQL below</b>, for the same
    /// reason `Stage25AddTierDownloadThresholds` was entirely hand-written: `tier_download_thresholds`
    /// has no EF entity at all (it is read by Dapper alone and written by a runbook script), so EF
    /// cannot see the column this item adds to it. The price seed is hand-written for the identical
    /// reason `Stage25AddPricedResourceCatalog`'s own seed is - a migration that must insert data, not
    /// only schema, because every charge site refuses cleanly when a key has no published version.</para>
    ///
    /// <para><b>The `sites.download_overage_billing_mode` default is `Manual`, and that is a decision,
    /// not an oversight</b> - see `Domain.DownloadOverageBillingMode`'s own remarks. `25-84` calls
    /// auto-bill "the recommended default"; defaulting the *column* to it would silently start charging
    /// every tenant that already exists, with their first notice arriving on an invoice. `Manual` is
    /// what every row behaves as today (`25-83`'s block), so this migration changes nobody's bill.</para>
    /// </summary>
    public partial class Stage25AddDownloadOverageBilling : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "download_overage_billing_mode",
                table: "sites",
                type: "text",
                nullable: false,
                defaultValue: "Manual");

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "download_overage_billing_mode_changed_at",
                table: "sites",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "download_overage_billing_mode_changed_by",
                table: "sites",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "download_overage_billing_mode_reason",
                table: "sites",
                type: "text",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "download_overage_charges",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    site_id = table.Column<Guid>(type: "uuid", nullable: false),
                    period_month = table.Column<DateOnly>(type: "date", nullable: false),
                    source = table.Column<string>(type: "text", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    bytes_over = table.Column<long>(type: "bigint", nullable: false),
                    amount_rub = table.Column<decimal>(type: "numeric(10,2)", precision: 10, scale: 2, nullable: false),
                    price_version = table.Column<int>(type: "integer", nullable: false),
                    yookassa_payment_id = table.Column<string>(type: "text", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    settled_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_download_overage_charges", x => x.id);
                    table.ForeignKey(
                        name: "FK_download_overage_charges_sites_site_id",
                        column: x => x.site_id,
                        principalTable: "sites",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_download_overage_charges_site_id_period_month",
                table: "download_overage_charges",
                columns: new[] { "site_id", "period_month" });

            migrationBuilder.CreateIndex(
                name: "ux_download_overage_charges_yookassa_payment_id",
                table: "download_overage_charges",
                column: "yookassa_payment_id",
                unique: true);

            // `tier_download_thresholds` has no EF entity (Stage25AddTierDownloadThresholds' own
            // remarks) - hand-written SQL is the only way to reach it. NULL means "uncapped", which is
            // available to the platform owner but is not what either seeded row gets.
            //
            // The two seeded figures are a labelled starting point, not a measured one - the identical
            // posture Stage25AddTierDownloadThresholds states for its own thresholds, and for the same
            // CLAUDE.md reason ("do not invent numbers... measure or stay silent"), so the derivation is
            // written down rather than the number alone:
            //
            //   'starter' -> 1000.00. The most this tier can cost in recurring subscription today is
            //   490 base + 2 extra seats * 200 = 890 RUB/month (SubscriptionTierBands: BaseSeats 3,
            //   MaxSeats 5, and the seat prices Stage25AddPricedResourceCatalog seeded). Rounded up to
            //   the nearest thousand, so auto-bill can at most roughly double what a tenant already
            //   agreed to pay in a month before the block returns.
            //
            //   'free' -> 0.00, i.e. auto-bill accrues nothing at all on the free tier. This is not a
            //   judgement call so much as a fact stated as data: auto-bill settles onto a renewal
            //   charge, a free-tier site has no subscription and no stored payment method, and so there
            //   is nothing for an accrued charge to land on. A free-tier tenant's only real route past
            //   the block is the manual checkout, which needs no stored payment method.
            migrationBuilder.Sql(
                """
                ALTER TABLE tier_download_thresholds
                    ADD COLUMN auto_bill_cap_rub numeric(10,2) NULL,
                    ADD CONSTRAINT ck_tier_download_thresholds_cap_not_negative
                        CHECK (auto_bill_cap_rub IS NULL OR auto_bill_cap_rub >= 0);

                UPDATE tier_download_thresholds SET auto_bill_cap_rub = 1000.00 WHERE tier = 'starter';
                UPDATE tier_download_thresholds SET auto_bill_cap_rub = 0.00 WHERE tier = 'free';
                """);

            // `25-84`'s own Done-when: "the per-GB overage price is configurable by the platform owner,
            // not hardcoded, with 100 RUB as the shipped default." The mechanism is `25-43`'s price
            // catalog, unchanged - this is only the `v1` row, seeded exactly the way
            // Stage25AddPricedResourceCatalog seeded the two seat keys, with fixed deterministic ids and
            // a fixed timestamp so the migration is identical in every environment it runs in. The
            // owner changes it afterwards through `PublishPriceVersion`/the owner pricing screen, which
            // already renders every key `PricedResourceKeys.All` lists and therefore needs no change of
            // its own to show this one.
            migrationBuilder.Sql(
                """
                INSERT INTO priced_resources (id, price_key, last_sequence)
                VALUES ('9c5b1b7e-2a3a-4b7a-9b1a-000000000003', 'download-overage-per-gb', 1);

                INSERT INTO published_price_versions (id, priced_resource_id, price_key, sequence, version, amount_rub, published_at)
                VALUES ('9c5b1b7e-2a3a-4b7a-9b1a-000000000013', '9c5b1b7e-2a3a-4b7a-9b1a-000000000003',
                        'download-overage-per-gb', 1, 'v1', 100.00, '2026-09-14T00:00:00+00:00');
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                DELETE FROM published_price_versions WHERE price_key = 'download-overage-per-gb';
                DELETE FROM priced_resources WHERE price_key = 'download-overage-per-gb';

                ALTER TABLE tier_download_thresholds
                    DROP CONSTRAINT ck_tier_download_thresholds_cap_not_negative,
                    DROP COLUMN auto_bill_cap_rub;
                """);

            migrationBuilder.DropTable(
                name: "download_overage_charges");

            migrationBuilder.DropColumn(
                name: "download_overage_billing_mode",
                table: "sites");

            migrationBuilder.DropColumn(
                name: "download_overage_billing_mode_changed_at",
                table: "sites");

            migrationBuilder.DropColumn(
                name: "download_overage_billing_mode_changed_by",
                table: "sites");

            migrationBuilder.DropColumn(
                name: "download_overage_billing_mode_reason",
                table: "sites");
        }
    }
}
