using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ago.Chat.Infrastructure.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class Stage25AddOperatorInviteEmailAndRevocation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // `25-73`'s own point 8: "every existing invite is annulled outright when this ships -
            // none in production are real yet, only test ones, so there is no migration to write and no
            // grandfathering to design." Deleted before `email` becomes NOT NULL, so the new column
            // never needs a placeholder default for a row that no longer exists by the time it is
            // added - not "add nullable, backfill, then enforce" (`db-migration` skill's own
            // additive-first rule for real data), because there is deliberately no data here to
            // preserve. Anyone redeploying this migration onto a database holding a real invite loses
            // it outright - stated here, in the release notes, and in this item's own report, not
            // silently.
            migrationBuilder.Sql("DELETE FROM operator_invites;");

            migrationBuilder.RenameIndex(
                name: "IX_operator_invites_site_id",
                table: "operator_invites",
                newName: "ix_operator_invites_site_id");

            migrationBuilder.AddColumn<string>(
                name: "email",
                table: "operator_invites",
                type: "text",
                nullable: false);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "revoked_at",
                table: "operator_invites",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "send_failure_code",
                table: "operator_invites",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        // One-way in substance, not only in form: this reverses the schema, but the `DELETE` above is
        // not something `Down` can undo - every invite this migration annulled stays gone (`db-migration`
        // skill's own "reversible, or explicitly marked one-way with a comment saying why").
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "email",
                table: "operator_invites");

            migrationBuilder.DropColumn(
                name: "revoked_at",
                table: "operator_invites");

            migrationBuilder.DropColumn(
                name: "send_failure_code",
                table: "operator_invites");

            migrationBuilder.RenameIndex(
                name: "ix_operator_invites_site_id",
                table: "operator_invites",
                newName: "IX_operator_invites_site_id");
        }
    }
}
