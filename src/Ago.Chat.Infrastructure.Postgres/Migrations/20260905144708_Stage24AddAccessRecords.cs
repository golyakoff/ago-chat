using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ago.Chat.Infrastructure.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class Stage24AddAccessRecords : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "access_records",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    occurred_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    access_kind = table.Column<string>(type: "text", nullable: false),
                    site_id = table.Column<Guid>(type: "uuid", nullable: true),
                    actor_kind = table.Column<string>(type: "text", nullable: false),
                    actor_id = table.Column<string>(type: "text", nullable: false),
                    resource_kind = table.Column<string>(type: "text", nullable: true),
                    resource_id = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_access_records", x => x.id);
                    table.CheckConstraint("ck_access_records_access_kind", "access_kind IN ('CrossConversationHistoryRead', 'OwnerSiteList', 'OwnerSiteDetail', 'OwnerModuleGrant', 'OwnerModuleRevoke', 'OwnerChannelIdentityUnlink')");
                    table.CheckConstraint("ck_access_records_actor_kind", "actor_kind IN ('Operator', 'PlatformOwner')");
                    table.CheckConstraint("ck_access_records_resource_kind", "resource_kind IS NULL OR resource_kind IN ('Conversation', 'ChannelIdentity', 'EnabledModule')");
                });

            migrationBuilder.CreateIndex(
                name: "ix_access_records_site_id_id",
                table: "access_records",
                columns: new[] { "site_id", "id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "access_records");
        }
    }
}
