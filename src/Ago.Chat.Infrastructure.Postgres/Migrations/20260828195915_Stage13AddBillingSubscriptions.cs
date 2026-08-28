using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ago.Chat.Infrastructure.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class Stage13AddBillingSubscriptions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "billing_subscriptions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    site_id = table.Column<Guid>(type: "uuid", nullable: false),
                    yookassa_payment_id = table.Column<string>(type: "text", nullable: false),
                    requested_seats = table.Column<int>(type: "integer", nullable: false),
                    tier = table.Column<string>(type: "text", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    payment_method_id = table.Column<string>(type: "text", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_billing_subscriptions", x => x.id);
                    table.ForeignKey(
                        name: "FK_billing_subscriptions_sites_site_id",
                        column: x => x.site_id,
                        principalTable: "sites",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "billing_webhook_events",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    yookassa_payment_id = table.Column<string>(type: "text", nullable: false),
                    event_type = table.Column<string>(type: "text", nullable: false),
                    received_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_billing_webhook_events", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_billing_subscriptions_site_id_created_at",
                table: "billing_subscriptions",
                columns: new[] { "site_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ux_billing_subscriptions_yookassa_payment_id",
                table: "billing_subscriptions",
                column: "yookassa_payment_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_billing_webhook_events_payment_id_event_type",
                table: "billing_webhook_events",
                columns: new[] { "yookassa_payment_id", "event_type" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "billing_subscriptions");

            migrationBuilder.DropTable(
                name: "billing_webhook_events");
        }
    }
}
