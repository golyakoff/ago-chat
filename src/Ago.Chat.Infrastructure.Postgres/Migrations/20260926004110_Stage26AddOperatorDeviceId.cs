using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ago.Chat.Infrastructure.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class Stage26AddOperatorDeviceId : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "device_id",
                table: "operator_devices",
                type: "character varying(256)",
                maxLength: 256,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ux_operator_devices_operator_device",
                table: "operator_devices",
                columns: new[] { "operator_id", "device_id" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ux_operator_devices_operator_device",
                table: "operator_devices");

            migrationBuilder.DropColumn(
                name: "device_id",
                table: "operator_devices");
        }
    }
}
