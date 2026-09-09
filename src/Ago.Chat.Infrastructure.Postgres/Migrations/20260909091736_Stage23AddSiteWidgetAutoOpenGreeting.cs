using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ago.Chat.Infrastructure.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class Stage23AddSiteWidgetAutoOpenGreeting : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "widget_auto_open_delay_seconds",
                table: "sites",
                type: "integer",
                nullable: false,
                defaultValue: 30);

            migrationBuilder.AddColumn<bool>(
                name: "widget_auto_open_enabled",
                table: "sites",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "widget_auto_open_greeting_text",
                table: "sites",
                type: "text",
                nullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "ck_sites_widget_auto_open_delay",
                table: "sites",
                sql: "widget_auto_open_delay_seconds IN (15, 30, 45, 60, 90, 120)");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_sites_widget_auto_open_delay",
                table: "sites");

            migrationBuilder.DropColumn(
                name: "widget_auto_open_delay_seconds",
                table: "sites");

            migrationBuilder.DropColumn(
                name: "widget_auto_open_enabled",
                table: "sites");

            migrationBuilder.DropColumn(
                name: "widget_auto_open_greeting_text",
                table: "sites");
        }
    }
}
