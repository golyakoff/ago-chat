using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ago.Chat.Infrastructure.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class Stage25AddSiteBranding : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "brand_company_name",
                table: "sites",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "logo_object_key",
                table: "sites",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "logo_rejection_reason",
                table: "sites",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "logo_status",
                table: "sites",
                type: "text",
                nullable: false,
                defaultValue: "None");

            migrationBuilder.AddColumn<string>(
                name: "pending_logo_object_key",
                table: "sites",
                type: "text",
                nullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "ck_sites_logo_status",
                table: "sites",
                sql: "logo_status IN ('None', 'Pending', 'Ready', 'Rejected')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_sites_logo_status",
                table: "sites");

            migrationBuilder.DropColumn(
                name: "brand_company_name",
                table: "sites");

            migrationBuilder.DropColumn(
                name: "logo_object_key",
                table: "sites");

            migrationBuilder.DropColumn(
                name: "logo_rejection_reason",
                table: "sites");

            migrationBuilder.DropColumn(
                name: "logo_status",
                table: "sites");

            migrationBuilder.DropColumn(
                name: "pending_logo_object_key",
                table: "sites");
        }
    }
}
