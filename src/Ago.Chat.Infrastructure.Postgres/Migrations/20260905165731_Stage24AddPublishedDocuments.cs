using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ago.Chat.Infrastructure.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class Stage24AddPublishedDocuments : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "documents",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    document_key = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    last_sequence = table.Column<int>(type: "integer", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_documents", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "published_document_versions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    document_id = table.Column<Guid>(type: "uuid", nullable: false),
                    document_key = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    sequence = table.Column<int>(type: "integer", nullable: false),
                    version = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    title = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    body = table.Column<string>(type: "character varying(100000)", maxLength: 100000, nullable: false),
                    published_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_published_document_versions", x => x.id);
                    table.ForeignKey(
                        name: "FK_published_document_versions_documents_document_id",
                        column: x => x.document_id,
                        principalTable: "documents",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_documents_key",
                table: "documents",
                column: "document_key",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_published_document_versions_document_id",
                table: "published_document_versions",
                column: "document_id");

            migrationBuilder.CreateIndex(
                name: "ix_published_document_versions_key_sequence",
                table: "published_document_versions",
                columns: new[] { "document_key", "sequence" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_published_document_versions_key_version",
                table: "published_document_versions",
                columns: new[] { "document_key", "version" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "published_document_versions");

            migrationBuilder.DropTable(
                name: "documents");
        }
    }
}
