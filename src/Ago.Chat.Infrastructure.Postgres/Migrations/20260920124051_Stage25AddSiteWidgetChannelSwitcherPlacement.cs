using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ago.Chat.Infrastructure.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class Stage25AddSiteWidgetChannelSwitcherPlacement : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "widget_channel_switcher_icon_size",
                table: "sites",
                type: "text",
                nullable: false,
                defaultValue: "Medium");

            migrationBuilder.AddColumn<string>(
                name: "widget_channel_switcher_placement",
                table: "sites",
                type: "text",
                nullable: false,
                defaultValue: "AboveComposer");

            migrationBuilder.AddCheckConstraint(
                name: "ck_sites_channel_switcher_icon_size",
                table: "sites",
                sql: "widget_channel_switcher_icon_size IN ('Large', 'Medium', 'Small')");

            migrationBuilder.AddCheckConstraint(
                name: "ck_sites_channel_switcher_placement",
                table: "sites",
                sql: "widget_channel_switcher_placement IN ('AboveComposer', 'BelowLauncher')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_sites_channel_switcher_icon_size",
                table: "sites");

            migrationBuilder.DropCheckConstraint(
                name: "ck_sites_channel_switcher_placement",
                table: "sites");

            migrationBuilder.DropColumn(
                name: "widget_channel_switcher_icon_size",
                table: "sites");

            migrationBuilder.DropColumn(
                name: "widget_channel_switcher_placement",
                table: "sites");
        }
    }
}
