using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ago.Chat.Infrastructure.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class Stage11AddSiteWidgetLocale : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "widget_locale",
                table: "sites",
                type: "text",
                nullable: false,
                defaultValue: "en");

            migrationBuilder.AddCheckConstraint(
                name: "ck_sites_widget_locale",
                table: "sites",
                sql: "widget_locale IN ('en', 'ru')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_sites_widget_locale",
                table: "sites");

            migrationBuilder.DropColumn(
                name: "widget_locale",
                table: "sites");
        }
    }
}
