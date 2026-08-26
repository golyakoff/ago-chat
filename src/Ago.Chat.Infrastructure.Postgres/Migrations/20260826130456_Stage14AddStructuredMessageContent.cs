using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ago.Chat.Infrastructure.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class Stage14AddStructuredMessageContent : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // `14-06`. Three nullable columns, no defaults, no backfill - which in Postgres is a
            // catalogue-only change with no table rewrite. That matters more here than anywhere else
            // in this schema: `messages` is the largest table in the system and it is
            // PARTITION BY RANGE (2-06), so a rewriting ALTER would have to rewrite every partition.
            // A NOT NULL column with a default would have been exactly that.
            //
            // `content` and `actions` are `text`, not `jsonb`, because nothing ever queries into
            // either - see MessageContentConverters for the full argument and data-model.md for the
            // recorded decision. `text` also means a payload over ~2 KB TOASTs out of line and
            // compressed, so a large one does not widen the heap the hot keyset read scans.
            migrationBuilder.AddColumn<string>(
                name: "actions",
                table: "messages",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "content",
                table: "messages",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "content_kind",
                table: "messages",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            // The storage backstop for the payload ceiling, and a deliberate second statement of a
            // number that also lives in MessagePayload.MaxLength.
            //
            // Two places for one limit is normally a drift hazard, and it is accepted here for the
            // reason data-model.md already states for this table's unique indexes: "anything
            // enforcing a guarantee gets a constraint, not just application code." The guarantee is
            // not validation politeness - it bounds an opaque field on the one write path that
            // accepts unauthenticated input from the public internet. The domain check is the
            // mechanism; this is what holds if a future writer ever reaches the table another way.
            //
            // `char_length`, not `octet_length`: MessagePayload counts UTF-16 chars, and matching
            // the unit is what keeps a payload the domain accepted from being refused here.
            migrationBuilder.Sql(
                "ALTER TABLE messages ADD CONSTRAINT ck_messages_content_length "
                + "CHECK (content IS NULL OR char_length(content) <= 16384);");

            // No matching constraint on `actions`: its bound is a *count* (MessageContent.MaxActions),
            // which a cheap CHECK cannot express, and its length is already bounded transitively by
            // that count times MessageAction's own two limits. Stated rather than left as an
            // asymmetry somebody has to rediscover.
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Dropping `content` would take the constraint with it; dropped explicitly first so the
            // Down reads as the exact inverse of the Up rather than as a side effect.
            migrationBuilder.Sql("ALTER TABLE messages DROP CONSTRAINT IF EXISTS ck_messages_content_length;");

            migrationBuilder.DropColumn(
                name: "actions",
                table: "messages");

            migrationBuilder.DropColumn(
                name: "content",
                table: "messages");

            migrationBuilder.DropColumn(
                name: "content_kind",
                table: "messages");
        }
    }
}
