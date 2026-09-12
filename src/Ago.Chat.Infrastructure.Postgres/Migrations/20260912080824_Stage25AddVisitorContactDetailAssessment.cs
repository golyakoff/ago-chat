using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ago.Chat.Infrastructure.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class Stage25AddVisitorContactDetailAssessment : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // `25-58`: hand-edited from EF's own generated `defaultValue: ""` - the same correction
            // `Stage23AddVisitorContactDetailSourceAndVerified`'s own comment made for `source`. Every
            // row that exists before this migration runs was recorded before this item ever added an
            // assessment concept, so the correct backfill is `Unset` - nobody has confirmed or flagged
            // any of them yet - never an empty string a real `VisitorContactDetailAssessment` member
            // never parses back from. The entity configuration deliberately declares no permanent
            // `HasDefaultValue` (every future insert sets `Assessment` explicitly via the domain
            // constructor), which is also why regenerating this migration from scratch would reproduce
            // the same wrong default - this line, not a `HasDefaultValue` in
            // `VisitorContactDetailConfiguration`, is what carries the correct backfill forward.
            migrationBuilder.AddColumn<string>(
                name: "assessment",
                table: "visitor_contact_details",
                type: "character varying(16)",
                maxLength: 16,
                nullable: false,
                defaultValue: "Unset");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "assessment",
                table: "visitor_contact_details");
        }
    }
}
