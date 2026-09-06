using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ago.Chat.Infrastructure.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class Stage23AddContactVisibilityRung : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "contact_visibility",
                table: "sites",
                type: "text",
                nullable: false,
                defaultValue: "Visible");

            migrationBuilder.CreateTable(
                name: "contact_reveals",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    occurred_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    site_id = table.Column<Guid>(type: "uuid", nullable: false),
                    conversation_id = table.Column<Guid>(type: "uuid", nullable: false),
                    contact_detail_id = table.Column<Guid>(type: "uuid", nullable: false),
                    operator_id = table.Column<Guid>(type: "uuid", nullable: false),
                    surface = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_contact_reveals", x => x.id);
                });

            migrationBuilder.AddCheckConstraint(
                name: "ck_sites_contact_visibility",
                table: "sites",
                sql: "contact_visibility IN ('Visible', 'MaskedWithReveal')");

            migrationBuilder.CreateIndex(
                name: "ix_contact_reveals_site_id_id",
                table: "contact_reveals",
                columns: new[] { "site_id", "id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "contact_reveals");

            migrationBuilder.DropCheckConstraint(
                name: "ck_sites_contact_visibility",
                table: "sites");

            migrationBuilder.DropColumn(
                name: "contact_visibility",
                table: "sites");
        }
    }
}
