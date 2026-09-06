using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ago.Chat.Infrastructure.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class Stage23AddTeamMessageRemoval : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "removed_at",
                table: "team_messages",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "team_message_removals",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    team_message_id = table.Column<Guid>(type: "uuid", nullable: false),
                    site_id = table.Column<Guid>(type: "uuid", nullable: false),
                    removed_by_operator_id = table.Column<Guid>(type: "uuid", nullable: false),
                    removed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_team_message_removals", x => x.id);
                    table.ForeignKey(
                        name: "FK_team_message_removals_operators_removed_by_operator_id",
                        column: x => x.removed_by_operator_id,
                        principalTable: "operators",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_team_message_removals_sites_site_id",
                        column: x => x.site_id,
                        principalTable: "sites",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_team_message_removals_team_messages_team_message_id",
                        column: x => x.team_message_id,
                        principalTable: "team_messages",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_team_message_removals_removed_by_operator_id",
                table: "team_message_removals",
                column: "removed_by_operator_id");

            migrationBuilder.CreateIndex(
                name: "ix_team_message_removals_site_id",
                table: "team_message_removals",
                column: "site_id");

            migrationBuilder.CreateIndex(
                name: "ix_team_message_removals_team_message_id",
                table: "team_message_removals",
                column: "team_message_id",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "team_message_removals");

            migrationBuilder.DropColumn(
                name: "removed_at",
                table: "team_messages");
        }
    }
}
