using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ago.Chat.Infrastructure.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class Stage23AddRoleChangeRecords : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "role_change_records",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    site_id = table.Column<Guid>(type: "uuid", nullable: false),
                    changed_by_operator_id = table.Column<Guid>(type: "uuid", nullable: false),
                    changed_operator_id = table.Column<Guid>(type: "uuid", nullable: false),
                    previous_role_names = table.Column<List<string>>(type: "text[]", nullable: false),
                    new_role_name = table.Column<string>(type: "text", nullable: false),
                    changed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_role_change_records", x => x.id);
                    table.ForeignKey(
                        name: "FK_role_change_records_operators_changed_by_operator_id",
                        column: x => x.changed_by_operator_id,
                        principalTable: "operators",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_role_change_records_operators_changed_operator_id",
                        column: x => x.changed_operator_id,
                        principalTable: "operators",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_role_change_records_sites_site_id",
                        column: x => x.site_id,
                        principalTable: "sites",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_role_change_records_changed_by_operator_id",
                table: "role_change_records",
                column: "changed_by_operator_id");

            migrationBuilder.CreateIndex(
                name: "IX_role_change_records_changed_operator_id",
                table: "role_change_records",
                column: "changed_operator_id");

            migrationBuilder.CreateIndex(
                name: "ix_role_change_records_site_id_changed_at",
                table: "role_change_records",
                columns: new[] { "site_id", "changed_at" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "role_change_records");
        }
    }
}
