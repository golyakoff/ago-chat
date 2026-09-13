using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ago.Chat.Infrastructure.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class Stage22AddTenantSuspension : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "suspended_until",
                table: "sites",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "site_suspensions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    site_id = table.Column<Guid>(type: "uuid", nullable: false),
                    action = table.Column<string>(type: "text", nullable: false),
                    performed_by = table.Column<string>(type: "text", nullable: false),
                    reason = table.Column<string>(type: "text", nullable: false),
                    suspended_until = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    performed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_site_suspensions", x => x.id);
                    table.ForeignKey(
                        name: "FK_site_suspensions_sites_site_id",
                        column: x => x.site_id,
                        principalTable: "sites",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_sites_suspended_until",
                table: "sites",
                column: "suspended_until",
                filter: "suspended_until IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_site_suspensions_site_id_performed_at",
                table: "site_suspensions",
                columns: new[] { "site_id", "performed_at" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "site_suspensions");

            migrationBuilder.DropIndex(
                name: "ix_sites_suspended_until",
                table: "sites");

            migrationBuilder.DropColumn(
                name: "suspended_until",
                table: "sites");
        }
    }
}
