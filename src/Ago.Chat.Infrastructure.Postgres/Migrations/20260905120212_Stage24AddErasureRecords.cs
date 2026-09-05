using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ago.Chat.Infrastructure.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class Stage24AddErasureRecords : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "erasure_record_id",
                table: "sites",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "erasure_requested_by",
                table: "sites",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "erasure_record_id",
                table: "conversations",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "erasure_requested_by",
                table: "conversations",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "erasure_records",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    scope = table.Column<string>(type: "text", nullable: false),
                    site_id = table.Column<Guid>(type: "uuid", nullable: false),
                    requested_by = table.Column<Guid>(type: "uuid", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    failure_reason = table.Column<string>(type: "text", nullable: true),
                    requested_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    completed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    messages_deleted = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    attachments_deleted = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    storage_objects_deleted = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    notes_deleted = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    tags_deleted = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    contact_details_deleted = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    conversations_marked_for_erasure = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    identities_deleted = table.Column<int>(type: "integer", nullable: false, defaultValue: 0)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_erasure_records", x => x.id);
                    table.CheckConstraint("ck_erasure_records_scope", "scope IN ('Conversation', 'Site')");
                    table.CheckConstraint("ck_erasure_records_status", "status IN ('Pending', 'Failed', 'Completed')");
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "erasure_records");

            migrationBuilder.DropColumn(
                name: "erasure_record_id",
                table: "sites");

            migrationBuilder.DropColumn(
                name: "erasure_requested_by",
                table: "sites");

            migrationBuilder.DropColumn(
                name: "erasure_record_id",
                table: "conversations");

            migrationBuilder.DropColumn(
                name: "erasure_requested_by",
                table: "conversations");
        }
    }
}
