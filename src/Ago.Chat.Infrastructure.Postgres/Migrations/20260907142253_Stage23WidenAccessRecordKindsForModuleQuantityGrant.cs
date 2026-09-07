using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ago.Chat.Infrastructure.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class Stage23WidenAccessRecordKindsForModuleQuantityGrant : Migration
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

            migrationBuilder.AddCheckConstraint(
                name: "ck_access_records_access_kind",
                table: "access_records",
                sql: "access_kind IN ('CrossConversationHistoryRead', 'OwnerSiteList', 'OwnerSiteDetail', 'OwnerModuleGrant', 'OwnerModuleRevoke', 'OwnerChannelIdentityUnlink', 'OwnerModuleQuantityGrant')");

            migrationBuilder.AddCheckConstraint(
                name: "ck_access_records_resource_kind",
                table: "access_records",
                sql: "resource_kind IS NULL OR resource_kind IN ('Conversation', 'ChannelIdentity', 'EnabledModule', 'ModuleQuantityGrant')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_access_records_access_kind",
                table: "access_records");

            migrationBuilder.DropCheckConstraint(
                name: "ck_access_records_resource_kind",
                table: "access_records");

            migrationBuilder.AddCheckConstraint(
                name: "ck_access_records_access_kind",
                table: "access_records",
                sql: "access_kind IN ('CrossConversationHistoryRead', 'OwnerSiteList', 'OwnerSiteDetail', 'OwnerModuleGrant', 'OwnerModuleRevoke', 'OwnerChannelIdentityUnlink')");

            migrationBuilder.AddCheckConstraint(
                name: "ck_access_records_resource_kind",
                table: "access_records",
                sql: "resource_kind IS NULL OR resource_kind IN ('Conversation', 'ChannelIdentity', 'EnabledModule')");
        }
    }
}
