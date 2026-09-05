using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ago.Chat.Infrastructure.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class Stage23AddChannelDeliveries : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "channel_deliveries",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    site_id = table.Column<Guid>(type: "uuid", nullable: false),
                    conversation_id = table.Column<Guid>(type: "uuid", nullable: false),
                    message_id = table.Column<Guid>(type: "uuid", nullable: false),
                    channel_kind = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    channel_identity_id = table.Column<Guid>(type: "uuid", nullable: false),
                    status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    provider_message_id = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    failure_reason = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    attempted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_channel_deliveries", x => x.id);
                    table.ForeignKey(
                        name: "FK_channel_deliveries_channel_identities_channel_identity_id",
                        column: x => x.channel_identity_id,
                        principalTable: "channel_identities",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_channel_deliveries_sites_site_id",
                        column: x => x.site_id,
                        principalTable: "sites",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_channel_deliveries_attempted_at",
                table: "channel_deliveries",
                column: "attempted_at");

            migrationBuilder.CreateIndex(
                name: "IX_channel_deliveries_channel_identity_id",
                table: "channel_deliveries",
                column: "channel_identity_id");

            migrationBuilder.CreateIndex(
                name: "ix_channel_deliveries_conversation_id_site_id_attempted_at",
                table: "channel_deliveries",
                columns: new[] { "conversation_id", "site_id", "attempted_at" });

            migrationBuilder.CreateIndex(
                name: "IX_channel_deliveries_site_id",
                table: "channel_deliveries",
                column: "site_id");

            migrationBuilder.CreateIndex(
                name: "ux_channel_deliveries_message_id",
                table: "channel_deliveries",
                column: "message_id",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "channel_deliveries");
        }
    }
}
