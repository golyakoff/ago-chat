using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ago.Chat.Infrastructure.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class Stage23AddAttachmentStorageQuotaAndDedup : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_attachments_site_id",
                table: "attachments");

            migrationBuilder.AddColumn<long>(
                name: "attachment_bytes_reserved",
                table: "sites",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<string>(
                name: "content_hash",
                table: "attachments",
                type: "text",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_attachments_site_content_hash",
                table: "attachments",
                columns: new[] { "site_id", "content_hash" },
                filter: "state = 'Ready' AND content_hash IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_attachments_site_content_hash",
                table: "attachments");

            migrationBuilder.DropColumn(
                name: "attachment_bytes_reserved",
                table: "sites");

            migrationBuilder.DropColumn(
                name: "content_hash",
                table: "attachments");

            migrationBuilder.CreateIndex(
                name: "IX_attachments_site_id",
                table: "attachments",
                column: "site_id");
        }
    }
}
