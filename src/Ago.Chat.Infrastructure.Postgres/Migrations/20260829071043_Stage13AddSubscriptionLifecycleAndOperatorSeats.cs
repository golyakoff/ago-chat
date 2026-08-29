using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ago.Chat.Infrastructure.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class Stage13AddSubscriptionLifecycleAndOperatorSeats : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_operators_site_id",
                table: "operators");

            migrationBuilder.AddColumn<bool>(
                name: "holds_seat",
                table: "operators",
                type: "boolean",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "removed_at",
                table: "operators",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "cancel_requested",
                table: "billing_subscriptions",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "current_period_end",
                table: "billing_subscriptions",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "last_renewal_attempt_at",
                table: "billing_subscriptions",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "past_due_since",
                table: "billing_subscriptions",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "pending_seat_count",
                table: "billing_subscriptions",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "pending_tier",
                table: "billing_subscriptions",
                type: "text",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_operators_site_id_removed_at",
                table: "operators",
                columns: new[] { "site_id", "removed_at" });

            migrationBuilder.CreateIndex(
                name: "ix_billing_subscriptions_status_current_period_end",
                table: "billing_subscriptions",
                columns: new[] { "status", "current_period_end" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_operators_site_id_removed_at",
                table: "operators");

            migrationBuilder.DropIndex(
                name: "ix_billing_subscriptions_status_current_period_end",
                table: "billing_subscriptions");

            migrationBuilder.DropColumn(
                name: "holds_seat",
                table: "operators");

            migrationBuilder.DropColumn(
                name: "removed_at",
                table: "operators");

            migrationBuilder.DropColumn(
                name: "cancel_requested",
                table: "billing_subscriptions");

            migrationBuilder.DropColumn(
                name: "current_period_end",
                table: "billing_subscriptions");

            migrationBuilder.DropColumn(
                name: "last_renewal_attempt_at",
                table: "billing_subscriptions");

            migrationBuilder.DropColumn(
                name: "past_due_since",
                table: "billing_subscriptions");

            migrationBuilder.DropColumn(
                name: "pending_seat_count",
                table: "billing_subscriptions");

            migrationBuilder.DropColumn(
                name: "pending_tier",
                table: "billing_subscriptions");

            migrationBuilder.CreateIndex(
                name: "IX_operators_site_id",
                table: "operators",
                column: "site_id");
        }
    }
}
