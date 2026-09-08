using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ago.Chat.Infrastructure.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class Stage23AddOperatorSeatRestoreOverridesAndAccessRecordKind : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_access_records_access_kind",
                table: "access_records");

            migrationBuilder.DropCheckConstraint(
                name: "ck_access_records_resource_kind",
                table: "access_records");

            migrationBuilder.CreateTable(
                name: "operator_seat_restore_overrides",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    site_id = table.Column<Guid>(type: "uuid", nullable: false),
                    operator_id = table.Column<Guid>(type: "uuid", nullable: false),
                    restored_by = table.Column<string>(type: "text", nullable: false),
                    reason = table.Column<string>(type: "text", nullable: false),
                    restored_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_operator_seat_restore_overrides", x => x.id);
                });

            migrationBuilder.AddCheckConstraint(
                name: "ck_access_records_access_kind",
                table: "access_records",
                sql: "access_kind IN ('CrossConversationHistoryRead', 'OwnerSiteList', 'OwnerSiteDetail', 'OwnerModuleGrant', 'OwnerModuleRevoke', 'OwnerChannelIdentityUnlink', 'OwnerModuleQuantityGrant', 'OwnerOperatorSeatRestore')");

            migrationBuilder.AddCheckConstraint(
                name: "ck_access_records_resource_kind",
                table: "access_records",
                sql: "resource_kind IS NULL OR resource_kind IN ('Conversation', 'ChannelIdentity', 'EnabledModule', 'ModuleQuantityGrant', 'Operator')");

            migrationBuilder.CreateIndex(
                name: "ix_operator_seat_restore_overrides_site_id",
                table: "operator_seat_restore_overrides",
                column: "site_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "operator_seat_restore_overrides");

            migrationBuilder.DropCheckConstraint(
                name: "ck_access_records_access_kind",
                table: "access_records");

            migrationBuilder.DropCheckConstraint(
                name: "ck_access_records_resource_kind",
                table: "access_records");

            migrationBuilder.AddCheckConstraint(
                name: "ck_access_records_access_kind",
                table: "access_records",
                sql: "access_kind IN ('CrossConversationHistoryRead', 'OwnerSiteList', 'OwnerSiteDetail', 'OwnerModuleGrant', 'OwnerModuleRevoke', 'OwnerChannelIdentityUnlink', 'OwnerModuleQuantityGrant')");

            migrationBuilder.AddCheckConstraint(
                name: "ck_access_records_resource_kind",
                table: "access_records",
                sql: "resource_kind IS NULL OR resource_kind IN ('Conversation', 'ChannelIdentity', 'EnabledModule', 'ModuleQuantityGrant')");
        }
    }
}
