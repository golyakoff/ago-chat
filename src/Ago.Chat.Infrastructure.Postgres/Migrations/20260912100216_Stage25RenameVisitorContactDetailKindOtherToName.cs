using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ago.Chat.Infrastructure.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class Stage25RenameVisitorContactDetailKindOtherToName : Migration
    {
        // `25-62`: a data fix, not a schema change - `kind` is, and stays, `character varying(32)`
        // storing the CLR member name (`VisitorContactDetailConfiguration`'s own
        // `HasConversion<string>()`), so renaming the C# enum member `Other` -> `Name` leaves the
        // model snapshot identical (this migration's own generated `Up`/`Down` came back empty from
        // `dotnet ef migrations add` before this SQL was hand-written in) while every existing row
        // still carries the literal string `"Other"`. Without this, `Enum.TryParse` on the read path
        // stops recognising those rows the moment the enum member disappears, and nothing written
        // going forward as `"Name"` would ever match them in a `WHERE kind = ...` query - the exact
        // silent-orphan failure the backlog item (`25-62`) exists to prevent.
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                "UPDATE visitor_contact_details SET kind = 'Name' WHERE kind = 'Other';");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                "UPDATE visitor_contact_details SET kind = 'Other' WHERE kind = 'Name';");
        }
    }
}
