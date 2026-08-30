using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ago.Chat.Infrastructure.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class Stage18AddConversationTrafficSource : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "traffic_referrer_host",
                table: "conversations",
                type: "character varying(512)",
                maxLength: 512,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "traffic_utm_campaign",
                table: "conversations",
                type: "character varying(512)",
                maxLength: 512,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "traffic_utm_medium",
                table: "conversations",
                type: "character varying(512)",
                maxLength: 512,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "traffic_utm_source",
                table: "conversations",
                type: "character varying(512)",
                maxLength: 512,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "traffic_referrer_host",
                table: "conversations");

            migrationBuilder.DropColumn(
                name: "traffic_utm_campaign",
                table: "conversations");

            migrationBuilder.DropColumn(
                name: "traffic_utm_medium",
                table: "conversations");

            migrationBuilder.DropColumn(
                name: "traffic_utm_source",
                table: "conversations");
        }
    }
}
