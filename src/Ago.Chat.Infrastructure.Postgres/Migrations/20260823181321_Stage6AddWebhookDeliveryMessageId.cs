using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ago.Chat.Infrastructure.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class Stage6AddWebhookDeliveryMessageId : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "message_id",
                table: "webhook_deliveries",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.CreateIndex(
                name: "ux_webhook_deliveries_endpoint_id_message_id",
                table: "webhook_deliveries",
                columns: new[] { "endpoint_id", "message_id" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ux_webhook_deliveries_endpoint_id_message_id",
                table: "webhook_deliveries");

            migrationBuilder.DropColumn(
                name: "message_id",
                table: "webhook_deliveries");
        }
    }
}
