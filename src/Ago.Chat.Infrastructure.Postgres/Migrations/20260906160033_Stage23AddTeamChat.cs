using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ago.Chat.Infrastructure.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class Stage23AddTeamChat : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "team_chat_last_sequence",
                table: "sites",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateTable(
                name: "team_messages",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    site_id = table.Column<Guid>(type: "uuid", nullable: false),
                    author_operator_id = table.Column<Guid>(type: "uuid", nullable: false),
                    author_is_admin = table.Column<bool>(type: "boolean", nullable: false),
                    body = table.Column<string>(type: "text", nullable: false),
                    sequence = table.Column<int>(type: "integer", nullable: false),
                    client_message_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_team_messages", x => x.id);
                    table.ForeignKey(
                        name: "FK_team_messages_operators_author_operator_id",
                        column: x => x.author_operator_id,
                        principalTable: "operators",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_team_messages_sites_site_id",
                        column: x => x.site_id,
                        principalTable: "sites",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_team_messages_author_operator_id",
                table: "team_messages",
                column: "author_operator_id");

            migrationBuilder.CreateIndex(
                name: "ix_team_messages_site_client_message_id",
                table: "team_messages",
                columns: new[] { "site_id", "client_message_id" },
                unique: true,
                filter: "client_message_id is not null");

            migrationBuilder.CreateIndex(
                name: "ix_team_messages_site_sequence",
                table: "team_messages",
                columns: new[] { "site_id", "sequence" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "team_messages");

            migrationBuilder.DropColumn(
                name: "team_chat_last_sequence",
                table: "sites");
        }
    }
}
