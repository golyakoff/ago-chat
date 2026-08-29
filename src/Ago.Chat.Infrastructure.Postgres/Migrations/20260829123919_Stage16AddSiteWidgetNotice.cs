using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ago.Chat.Infrastructure.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class Stage16AddSiteWidgetNotice : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "widget_notice_text",
                table: "sites",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "widget_notice_url",
                table: "sites",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "widget_notice_text",
                table: "sites");

            migrationBuilder.DropColumn(
                name: "widget_notice_url",
                table: "sites");
        }
    }
}
