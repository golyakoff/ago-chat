using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ago.Chat.Infrastructure.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class Stage23AddVisitorRestrictions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "routing_suppressed_at",
                table: "conversations",
                type: "timestamp with time zone",
                nullable: true);

            // `23-69`/`23-77`: visitor_restrictions has no EF entity behind it - IVisitorRestrictionRepository's
            // own remarks explain why (raw SQL end to end, the same shape conversation_block_records/
            // access_records already use) - so, like Stage24AddConversationBlocking's own
            // conversation_block_records table, this CreateTable is hand-added rather than generated.
            migrationBuilder.CreateTable(
                name: "visitor_restrictions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    site_id = table.Column<Guid>(type: "uuid", nullable: false),
                    visitor_id = table.Column<Guid>(type: "uuid", nullable: false),
                    kind = table.Column<string>(type: "text", nullable: false),
                    restricted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    restricted_by = table.Column<Guid>(type: "uuid", nullable: false),
                    // Nullable on purpose - null is `23-77`'s own indefinite Block; a real timestamp is
                    // `23-69`'s own time-windowed Spam mute (IVisitorRestrictionRepository's own remarks).
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    source_conversation_id = table.Column<Guid>(type: "uuid", nullable: false),
                    lifted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    lifted_by = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_visitor_restrictions", x => x.id);
                    table.CheckConstraint("ck_visitor_restrictions_kind", "kind IN ('Spam', 'Block')");
                    table.ForeignKey(
                        name: "FK_visitor_restrictions_sites_site_id",
                        column: x => x.site_id,
                        principalTable: "sites",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_visitor_restrictions_visitors_visitor_id",
                        column: x => x.visitor_id,
                        principalTable: "visitors",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    // No foreign key to conversations: source_conversation_id must remain nameable even
                    // after the conversation it names is later erased (16-02) - the identical
                    // survive-the-subject's-own-erasure reasoning AccessRecordToWrite's own remarks give
                    // in full for its own ResourceId.
                });

            // The enforcement read (IVisitorRestrictionRepository.IsActiveAsync/GetActiveKindAsync) is
            // always `site_id = ... and visitor_id = ...`, exactly this pair - StartConversationHandler's
            // own hot path. No further columns joined: `lifted_at`/`expires_at` are checked in memory
            // against `now`, not filtered by the index itself, since a partial index cannot depend on a
            // runtime value.
            migrationBuilder.CreateIndex(
                name: "ix_visitor_restrictions_site_visitor",
                table: "visitor_restrictions",
                columns: new[] { "site_id", "visitor_id" });

            // The tenant's own console read (ListForSiteAsync) keysets by id, scoped to one site - the
            // identical ix_access_records_site_id_id shape Stage24AddAccessRecords already uses.
            migrationBuilder.CreateIndex(
                name: "ix_visitor_restrictions_site_id_id",
                table: "visitor_restrictions",
                columns: new[] { "site_id", "id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "visitor_restrictions");

            migrationBuilder.DropColumn(
                name: "routing_suppressed_at",
                table: "conversations");
        }
    }
}
