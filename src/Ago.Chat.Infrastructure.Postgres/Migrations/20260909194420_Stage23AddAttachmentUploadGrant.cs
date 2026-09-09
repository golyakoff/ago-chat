using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ago.Chat.Infrastructure.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class Stage23AddAttachmentUploadGrant : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "widget_allow_attachment_uploads_by_default",
                table: "sites",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "attachment_upload_granted_at",
                table: "conversations",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "attachment_upload_granted_by",
                table: "conversations",
                type: "uuid",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "widget_allow_attachment_uploads_by_default",
                table: "sites");

            migrationBuilder.DropColumn(
                name: "attachment_upload_granted_at",
                table: "conversations");

            migrationBuilder.DropColumn(
                name: "attachment_upload_granted_by",
                table: "conversations");
        }
    }
}
