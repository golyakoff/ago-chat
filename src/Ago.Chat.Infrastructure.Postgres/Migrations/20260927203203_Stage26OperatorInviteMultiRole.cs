using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ago.Chat.Infrastructure.Postgres.Migrations
{
    /// <summary>`26-241`: the operator invite's single `role_id` column becomes a one-to-many child
    /// table `operator_invite_roles`, so an admin can invite a colleague to more than one role at once.
    /// Data-preserving: the new table is created and every existing invite's `role_id` copied into it
    /// before the old column is dropped (the scaffolded default order dropped first and would have lost
    /// every existing invite's role).</summary>
    /// <inheritdoc />
    public partial class Stage26OperatorInviteMultiRole : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "operator_invite_roles",
                columns: table => new
                {
                    operator_invite_id = table.Column<System.Guid>(type: "uuid", nullable: false),
                    role_id = table.Column<System.Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_operator_invite_roles", x => new { x.operator_invite_id, x.role_id });
                    table.ForeignKey(
                        name: "FK_operator_invite_roles_operator_invites_operator_invite_id",
                        column: x => x.operator_invite_id,
                        principalTable: "operator_invites",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_operator_invite_roles_roles_role_id",
                        column: x => x.role_id,
                        principalTable: "roles",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_operator_invite_roles_role_id",
                table: "operator_invite_roles",
                column: "role_id");

            // `26-241`: carry every existing invite's single role into the new table before the column it
            // came from is dropped - so no pending invite loses the role it was issued for.
            migrationBuilder.Sql(
                "INSERT INTO operator_invite_roles (operator_invite_id, role_id) "
                + "SELECT id, role_id FROM operator_invites;");

            migrationBuilder.DropForeignKey(
                name: "FK_operator_invites_roles_role_id",
                table: "operator_invites");

            migrationBuilder.DropIndex(
                name: "IX_operator_invites_role_id",
                table: "operator_invites");

            migrationBuilder.DropColumn(
                name: "role_id",
                table: "operator_invites");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<System.Guid>(
                name: "role_id",
                table: "operator_invites",
                type: "uuid",
                nullable: false,
                defaultValue: new System.Guid("00000000-0000-0000-0000-000000000000"));

            // Collapse the multi-role child rows back to one role per invite (the first by role id) before
            // re-adding the foreign key, so a real, resolvable role id sits in the restored column. An
            // invite that was multi-role loses its extra roles - inherent to reverting to a single-role
            // column, acceptable for a Down.
            migrationBuilder.Sql(
                "UPDATE operator_invites oi SET role_id = ("
                + "SELECT oir.role_id FROM operator_invite_roles oir "
                + "WHERE oir.operator_invite_id = oi.id ORDER BY oir.role_id LIMIT 1) "
                + "WHERE EXISTS (SELECT 1 FROM operator_invite_roles oir WHERE oir.operator_invite_id = oi.id);");

            migrationBuilder.DropTable(
                name: "operator_invite_roles");

            migrationBuilder.CreateIndex(
                name: "IX_operator_invites_role_id",
                table: "operator_invites",
                column: "role_id");

            migrationBuilder.AddForeignKey(
                name: "FK_operator_invites_roles_role_id",
                table: "operator_invites",
                column: "role_id",
                principalTable: "roles",
                principalColumn: "id",
                onDelete: ReferentialAction.Cascade);
        }
    }
}
