using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ago.Chat.Infrastructure.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class Stage23AddVisitorContactDetailSourceAndVerified : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<Guid>(
                name: "recorded_by_operator_id",
                table: "visitor_contact_details",
                type: "uuid",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "uuid");

            // `23-09`: hand-edited from EF's own generated `defaultValue: ""` - every row that exists
            // before this migration runs was recorded the only way `14-14` ever offered, an operator
            // typing it down, so the correct backfill for `source` is "Operator", never an empty
            // string a real VisitorContactDetailSource member never parses back from. EF has no way to
            // infer this: the entity configuration deliberately does not declare a permanent
            // HasDefaultValue (every future insert sets Source explicitly, so there is nothing for a
            // standing database default to do), which is also why regenerating this migration from
            // scratch would reproduce the same wrong default - this line, not a HasDefaultValue in
            // VisitorContactDetailConfiguration, is what carries the correct backfill forward.
            migrationBuilder.AddColumn<string>(
                name: "source",
                table: "visitor_contact_details",
                type: "character varying(16)",
                maxLength: 16,
                nullable: false,
                defaultValue: "Operator");

            migrationBuilder.AddColumn<bool>(
                name: "verified",
                table: "visitor_contact_details",
                type: "boolean",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "source",
                table: "visitor_contact_details");

            migrationBuilder.DropColumn(
                name: "verified",
                table: "visitor_contact_details");

            migrationBuilder.AlterColumn<Guid>(
                name: "recorded_by_operator_id",
                table: "visitor_contact_details",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"),
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldNullable: true);
        }
    }
}
