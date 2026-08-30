using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ago.Chat.Infrastructure.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class Stage14AddVisitorPreferredChannelIdentity : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "preferred_channel_identity_id",
                table: "visitors",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_visitors_preferred_channel_identity_id",
                table: "visitors",
                column: "preferred_channel_identity_id");

            migrationBuilder.AddForeignKey(
                name: "FK_visitors_channel_identities_preferred_channel_identity_id",
                table: "visitors",
                column: "preferred_channel_identity_id",
                principalTable: "channel_identities",
                principalColumn: "id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_visitors_channel_identities_preferred_channel_identity_id",
                table: "visitors");

            migrationBuilder.DropIndex(
                name: "IX_visitors_preferred_channel_identity_id",
                table: "visitors");

            migrationBuilder.DropColumn(
                name: "preferred_channel_identity_id",
                table: "visitors");
        }
    }
}
