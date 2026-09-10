using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ago.Chat.Infrastructure.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class Stage25AddPricedResourceCatalog : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "base_seat_price_version",
                table: "billing_subscriptions",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "extra_seat_price_version",
                table: "billing_subscriptions",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateTable(
                name: "priced_resources",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    price_key = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    last_sequence = table.Column<int>(type: "integer", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_priced_resources", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "published_price_versions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    priced_resource_id = table.Column<Guid>(type: "uuid", nullable: false),
                    price_key = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    sequence = table.Column<int>(type: "integer", nullable: false),
                    version = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    amount_rub = table.Column<decimal>(type: "numeric(10,2)", precision: 10, scale: 2, nullable: false),
                    published_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_published_price_versions", x => x.id);
                    table.ForeignKey(
                        name: "FK_published_price_versions_priced_resources_priced_resource_id",
                        column: x => x.priced_resource_id,
                        principalTable: "priced_resources",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_priced_resources_key",
                table: "priced_resources",
                column: "price_key",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_published_price_versions_key_sequence",
                table: "published_price_versions",
                columns: new[] { "price_key", "sequence" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_published_price_versions_priced_resource_id",
                table: "published_price_versions",
                column: "priced_resource_id");

            // `25-43`: seeds `v1` for both seat-pricing keys, at the exact numbers `25-29` hand-
            // corrected into `BillingOptions`/`appsettings` before this mechanism existed
            // (`ago-business` decision `0012`: 490 base, 200 marginal) - the one migration in this
            // codebase that must insert data, not only schema, because every real charge site now
            // refuses cleanly when a key has no published version (`PriceCatalogErrors.
            // PriceNotConfigured`), and this deployment already has a real, live BillingSubscription
            // history charging those exact two numbers. Without this seed, the very next checkout or
            // renewal after this migration ships would refuse every single charge - not a "not yet for
            // sale" resource that has genuinely never been priced, which `25-43`'s own second decision
            // says is fine, but a regression on a resource this codebase has been charging for
            // pre-`25-43` under `BillingOptions`'s own former field. A fixed, deterministic id and
            // timestamp (rather than gen_random_uuid()/now()) is what makes this migration itself
            // idempotent-reviewable and identical across every environment it runs in.
            migrationBuilder.Sql("""
                INSERT INTO priced_resources (id, price_key, last_sequence)
                VALUES
                    ('9c5b1b7e-2a3a-4b7a-9b1a-000000000001', 'seat-base', 1),
                    ('9c5b1b7e-2a3a-4b7a-9b1a-000000000002', 'seat-extra', 1);

                INSERT INTO published_price_versions (id, priced_resource_id, price_key, sequence, version, amount_rub, published_at)
                VALUES
                    ('9c5b1b7e-2a3a-4b7a-9b1a-000000000011', '9c5b1b7e-2a3a-4b7a-9b1a-000000000001', 'seat-base', 1, 'v1', 490.00, '2026-09-10T00:00:00+00:00'),
                    ('9c5b1b7e-2a3a-4b7a-9b1a-000000000012', '9c5b1b7e-2a3a-4b7a-9b1a-000000000002', 'seat-extra', 1, 'v1', 200.00, '2026-09-10T00:00:00+00:00');
                """);

            // `25-43`: backfills every pre-existing base subscription row (an option row's own
            // base_seat_price_version/extra_seat_price_version stays 0 - "meaningless for an option
            // row", the same convention RequestedSeats/Tier already use for one) to point at the `v1`
            // version just seeded above, rather than leaving the AddColumn default of 0 in place. Not
            // a grandfathering violation: every such row was, in fact, already being charged 490/200
            // before this migration (`25-29`'s own hand correction to `BillingOptions` landed first) -
            // this UPDATE states that historical fact as this mechanism's own data, it does not change
            // what anyone was actually charged. Left at 0, the next seat-count upgrade for one of these
            // rows would resolve `FindVersionAsync(key, 0)` to null and throw ("a published price
            // version must never be deleted") - a real regression this backfill exists to prevent, not
            // a theoretical one.
            migrationBuilder.Sql("""
                UPDATE billing_subscriptions
                SET base_seat_price_version = 1, extra_seat_price_version = 1
                WHERE option_key IS NULL AND base_seat_price_version = 0;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "published_price_versions");

            migrationBuilder.DropTable(
                name: "priced_resources");

            migrationBuilder.DropColumn(
                name: "base_seat_price_version",
                table: "billing_subscriptions");

            migrationBuilder.DropColumn(
                name: "extra_seat_price_version",
                table: "billing_subscriptions");
        }
    }
}
