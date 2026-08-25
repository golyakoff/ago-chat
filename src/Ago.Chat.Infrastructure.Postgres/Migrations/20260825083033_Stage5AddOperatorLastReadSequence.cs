using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ago.Chat.Infrastructure.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class Stage5AddOperatorLastReadSequence : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // `5-15`: additive, reversible, and `NOT NULL DEFAULT 0` on purpose. Zero is not a
            // placeholder here - it is the truthful value for every existing row: nothing has ever
            // marked a conversation read, so no operator has a read position yet. That means the
            // first open after this ships clears the whole accumulated backlog `5-15` was written
            // about, which is the intended outcome, not a side effect to guard against. No backfill
            // from `last_sequence` for exactly that reason: claiming operators had read everything
            // would be inventing a fact (`CLAUDE.md`).
            migrationBuilder.AddColumn<int>(
                name: "operator_last_read_sequence",
                table: "conversations",
                type: "integer",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "operator_last_read_sequence",
                table: "conversations");
        }
    }
}
