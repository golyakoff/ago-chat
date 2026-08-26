using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ago.Chat.Infrastructure.Postgres.Migrations
{
    /// <summary>
    /// `14-01`: AGO Inbox's one new table - which external chat-id or phone number corresponds to
    /// which visitor (<c>Ago.Chat.Domain.ChannelIdentity</c>, `adr/0055`).
    ///
    /// <para>Additive and fully reversible: a new table with two foreign keys and no change to any
    /// existing column, so <c>Down</c> is a single <c>DROP TABLE</c> that genuinely restores the prior
    /// schema - unlike `2-06`'s partitioning conversion, which had to be marked one-way. Nothing to
    /// backfill, because no existing row has ever been reached through a channel.</para>
    ///
    /// <para><c>ux_channel_identities_site_kind_address</c> is the load-bearing constraint: it is both
    /// the lookup <c>IChannelIdentityRepository.FindAsync</c> serves and the storage-level backstop
    /// that stops two processes racing the same first inbound message from creating two visitors for
    /// one person (`adr/0019`'s "the index is the backstop, not the primary mechanism" division).
    /// <c>IX_channel_identities_visitor_id</c> is EF's default foreign-key index, kept rather than
    /// suppressed: "which channels is this visitor reachable on" is the natural inverse query and the
    /// table is small relative to <c>messages</c>.</para>
    /// </summary>
    public partial class Stage14AddChannelIdentities : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "channel_identities",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    site_id = table.Column<Guid>(type: "uuid", nullable: false),
                    kind = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    external_address = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    visitor_id = table.Column<Guid>(type: "uuid", nullable: false),
                    first_seen_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    last_seen_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_channel_identities", x => x.id);
                    table.ForeignKey(
                        name: "FK_channel_identities_sites_site_id",
                        column: x => x.site_id,
                        principalTable: "sites",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_channel_identities_visitors_visitor_id",
                        column: x => x.visitor_id,
                        principalTable: "visitors",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_channel_identities_visitor_id",
                table: "channel_identities",
                column: "visitor_id");

            migrationBuilder.CreateIndex(
                name: "ux_channel_identities_site_kind_address",
                table: "channel_identities",
                columns: new[] { "site_id", "kind", "external_address" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "channel_identities");
        }
    }
}
