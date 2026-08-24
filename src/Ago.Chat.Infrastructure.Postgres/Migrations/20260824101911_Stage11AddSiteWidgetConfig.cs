using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ago.Chat.Infrastructure.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class Stage11AddSiteWidgetConfig : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "widget_position",
                table: "sites",
                type: "text",
                nullable: false,
                defaultValue: "bottom-right");

            migrationBuilder.AddColumn<string>(
                name: "widget_primary_color_hex",
                table: "sites",
                type: "text",
                nullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "ck_sites_widget_position",
                table: "sites",
                sql: "widget_position IN ('bottom-right', 'bottom-left')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_sites_widget_position",
                table: "sites");

            migrationBuilder.DropColumn(
                name: "widget_position",
                table: "sites");

            migrationBuilder.DropColumn(
                name: "widget_primary_color_hex",
                table: "sites");
        }
    }
}
