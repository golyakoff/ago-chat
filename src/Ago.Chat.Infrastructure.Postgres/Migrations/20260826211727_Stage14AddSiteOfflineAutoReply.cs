using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ago.Chat.Infrastructure.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class Stage14AddSiteOfflineAutoReply : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "offline_auto_reply_enabled",
                table: "sites",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "offline_auto_reply_fallback",
                table: "sites",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "offline_auto_reply_rules",
                table: "sites",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "offline_auto_reply_enabled",
                table: "sites");

            migrationBuilder.DropColumn(
                name: "offline_auto_reply_fallback",
                table: "sites");

            migrationBuilder.DropColumn(
                name: "offline_auto_reply_rules",
                table: "sites");
        }
    }
}
