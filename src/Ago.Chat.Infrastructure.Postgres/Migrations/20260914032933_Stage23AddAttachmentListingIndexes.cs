using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ago.Chat.Infrastructure.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class Stage23AddAttachmentListingIndexes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "ix_attachments_site_state_created_download",
                table: "attachments",
                columns: new[] { "site_id", "state", "created_at", "download_count" },
                filter: "state = 'Ready'");

            migrationBuilder.CreateIndex(
                name: "ix_attachments_site_state_size",
                table: "attachments",
                columns: new[] { "site_id", "state", "size_bytes" },
                filter: "state = 'Ready'");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_attachments_site_state_created_download",
                table: "attachments");

            migrationBuilder.DropIndex(
                name: "ix_attachments_site_state_size",
                table: "attachments");
        }
    }
}
