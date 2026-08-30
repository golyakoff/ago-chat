using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ago.Chat.Infrastructure.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class Stage14AddVerifiedChannelIdentityLinking : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ux_channel_identities_site_kind_address",
                table: "channel_identities");

            // `14-12`: every pre-existing row predates the Active/UnlinkedAt concept and was, by
            // definition, a real, currently-in-effect link - defaultValue: true, not the scaffolded
            // false, so this migration does not silently unlink every ChannelIdentity that already
            // existed. A backfill this important is stated here rather than left to the tool's own
            // (wrong, for this column) default.
            migrationBuilder.AddColumn<bool>(
                name: "active",
                table: "channel_identities",
                type: "boolean",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "unlinked_at",
                table: "channel_identities",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "pending_channel_link_requests",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    site_id = table.Column<Guid>(type: "uuid", nullable: false),
                    visitor_id = table.Column<Guid>(type: "uuid", nullable: false),
                    kind = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    code_hash = table.Column<byte[]>(type: "bytea", nullable: false),
                    requested_by_operator_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    consumed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_pending_channel_link_requests", x => x.id);
                    table.ForeignKey(
                        name: "FK_pending_channel_link_requests_sites_site_id",
                        column: x => x.site_id,
                        principalTable: "sites",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_pending_channel_link_requests_visitors_visitor_id",
                        column: x => x.visitor_id,
                        principalTable: "visitors",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ux_channel_identities_site_kind_address_active",
                table: "channel_identities",
                columns: new[] { "site_id", "kind", "external_address" },
                unique: true,
                filter: "active");

            migrationBuilder.CreateIndex(
                name: "ix_pending_channel_link_requests_site_kind_code_hash",
                table: "pending_channel_link_requests",
                columns: new[] { "site_id", "kind", "code_hash" });

            migrationBuilder.CreateIndex(
                name: "IX_pending_channel_link_requests_visitor_id",
                table: "pending_channel_link_requests",
                column: "visitor_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "pending_channel_link_requests");

            migrationBuilder.DropIndex(
                name: "ux_channel_identities_site_kind_address_active",
                table: "channel_identities");

            migrationBuilder.DropColumn(
                name: "active",
                table: "channel_identities");

            migrationBuilder.DropColumn(
                name: "unlinked_at",
                table: "channel_identities");

            migrationBuilder.CreateIndex(
                name: "ux_channel_identities_site_kind_address",
                table: "channel_identities",
                columns: new[] { "site_id", "kind", "external_address" },
                unique: true);
        }
    }
}
