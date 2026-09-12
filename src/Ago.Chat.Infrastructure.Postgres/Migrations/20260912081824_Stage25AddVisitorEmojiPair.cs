using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ago.Chat.Infrastructure.Postgres.Migrations
{
    /// <summary>
    /// `25-56`: every visitor gets a stable, operator-facing emoji pair, drawn from
    /// <see cref="VisitorEmojiDictionary"/>'s two fixed lists, assigned once and never reassigned
    /// (`Visitor.AssignEmojiPair`'s own remarks). New rows arrive with a pair already assigned - both
    /// real creation call sites (<c>StartConversationHandler</c>, <c>ReceiveChannelMessageHandler</c>)
    /// call <c>AssignEmojiPair</c> before the first save - so this migration's own job is the
    /// existing-row backfill the backlog item's Done-when demands ("existing visitors get a real,
    /// stated backfill strategy, not a permanently empty pair").
    ///
    /// <para><b>Both columns stay nullable - deliberately not tightened to NOT NULL afterward, unlike
    /// `Stage25AddSiteAdminLimit`'s own precedent.</b> A NOT NULL constraint would have to hold for
    /// every row any test in this repository ever inserts, and roughly ninety existing test call sites
    /// across this codebase construct a bare <c>Visitor</c> for fixture scaffolding that has nothing to
    /// do with its emoji pair - trying it against a real Postgres surfaced exactly that failure
    /// (`23502: null value in column "emoji_creature"`) the first time this migration ran against
    /// Testcontainers. <see cref="Persistence.VisitorConfiguration"/>'s own remarks make the identical
    /// call for the EF model: the guarantee this system actually needs - every visitor a real caller can
    /// ever observe has a pair - is enforced by this migration's backfill plus both real creation call
    /// sites, not by the schema.</para>
    ///
    /// <para><b>The SQL below is a one-time copy of <see cref="VisitorEmojiDictionary"/>'s own two
    /// lists, not a reference to them.</b> A migration already applied to a real database is never
    /// edited (`db-migration` skill) - if that C# list is ever extended or trimmed later, this
    /// migration keeps assigning from the twenty-and-twenty snapshot it shipped with, which is correct:
    /// every row it touches already has a real, valid pair the moment this migration finishes, and
    /// nothing about a later dictionary change is retroactive for a visitor already assigned one
    /// (decision 5 - "permanent for that visitor").</para>
    ///
    /// <para><b>Not batched.</b> `db-migration`'s own batching rule is for a backfill large enough that
    /// one `UPDATE` would hold a long-lived lock or a huge undo buffer - this repository's own real
    /// data (a demo/portfolio tenant's own visitors, `docs/vision.md`) is nowhere near that scale, the
    /// same call `Stage25AddSiteAdminLimit`'s own unconditional, unbatched `sites` backfill already
    /// made for a comparably-sized table.</para>
    /// </summary>
    public partial class Stage25AddVisitorEmojiPair : Migration
    {
        // The exact members of VisitorEmojiDictionary.Creatures/Foods at the moment this migration was
        // authored - see this migration's own class remarks for why a copy, not a reference, is correct.
        private const string CreaturesArrayLiteral =
            "array['🐔','🐠','🐳','🐶','🐱','🐭','🐹','🐰','🦊','🐻','🐼','🐨','🐯','🦁','🐮','🐷','🐸','🐵','🐦','🦉']";

        private const string FoodsArrayLiteral =
            "array['🍊','🥝','🌭','🍕','🍔','🍟','🌮','🍣','🍩','🍪','🍦','🍎','🍌','🍇','🍉','🍓','🍒','🍑','🥑','🍍']";

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "emoji_creature",
                table: "visitors",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "emoji_food",
                table: "visitors",
                type: "text",
                nullable: true);

            // The backfill. `floor(random() * 20)::int` is a uniform pick over a fixed twenty-member
            // array, 1-indexed (Postgres arrays start at 1) - the identical "one random member of a
            // fixed list" IVisitorEmojiPairGenerator's own real implementation performs at the
            // application layer, run once here for every row this migration finds still null.
            // `WHERE emoji_creature IS NULL` makes this idempotent (safe to reason about even though
            // this migration only ever runs once per database, the same defensive habit
            // `Stage25AddSiteAdminLimit`'s own backfill states for itself).
            migrationBuilder.Sql($"""
                UPDATE visitors
                SET emoji_creature = ({CreaturesArrayLiteral})[1 + floor(random() * 20)::int],
                    emoji_food = ({FoodsArrayLiteral})[1 + floor(random() * 20)::int]
                WHERE emoji_creature IS NULL;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "emoji_creature",
                table: "visitors");

            migrationBuilder.DropColumn(
                name: "emoji_food",
                table: "visitors");
        }
    }
}
