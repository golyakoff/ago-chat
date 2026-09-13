using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ago.Chat.Infrastructure.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class Stage25AddRolePermissionRemovalOverridesAndAccessRecordKind : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_access_records_access_kind",
                table: "access_records");

            migrationBuilder.CreateTable(
                name: "role_permission_removal_overrides",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    site_id = table.Column<Guid>(type: "uuid", nullable: false),
                    role_name = table.Column<string>(type: "text", nullable: false),
                    permissions = table.Column<List<string>>(type: "text[]", nullable: false),
                    removed_by = table.Column<string>(type: "text", nullable: false),
                    reason = table.Column<string>(type: "text", nullable: false),
                    removed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_role_permission_removal_overrides", x => x.id);
                });

            migrationBuilder.AddCheckConstraint(
                name: "ck_access_records_access_kind",
                table: "access_records",
                sql: "access_kind IN ('CrossConversationHistoryRead', 'OwnerSiteList', 'OwnerSiteDetail', 'OwnerModuleGrant', 'OwnerModuleRevoke', 'OwnerChannelIdentityUnlink', 'OwnerModuleQuantityGrant', 'OwnerOperatorSeatRestore', 'OwnerRolePermissionsGrant', 'OwnerRolePermissionsRemoval')");

            migrationBuilder.CreateIndex(
                name: "ix_role_permission_removal_overrides_site_id",
                table: "role_permission_removal_overrides",
                column: "site_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "role_permission_removal_overrides");

            migrationBuilder.DropCheckConstraint(
                name: "ck_access_records_access_kind",
                table: "access_records");

            migrationBuilder.AddCheckConstraint(
                name: "ck_access_records_access_kind",
                table: "access_records",
                sql: "access_kind IN ('CrossConversationHistoryRead', 'OwnerSiteList', 'OwnerSiteDetail', 'OwnerModuleGrant', 'OwnerModuleRevoke', 'OwnerChannelIdentityUnlink', 'OwnerModuleQuantityGrant', 'OwnerOperatorSeatRestore', 'OwnerRolePermissionsGrant')");
        }
    }
}
