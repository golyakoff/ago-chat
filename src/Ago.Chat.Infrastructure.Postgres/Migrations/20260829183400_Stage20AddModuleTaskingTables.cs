using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ago.Chat.Infrastructure.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class Stage20AddModuleTaskingTables : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "enabled_modules",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    site_id = table.Column<Guid>(type: "uuid", nullable: false),
                    module_key = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    trigger_words = table.Column<string>(type: "text", nullable: false),
                    entry_point = table.Column<string>(type: "text", nullable: false),
                    enabled_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_enabled_modules", x => x.id);
                    table.ForeignKey(
                        name: "FK_enabled_modules_sites_site_id",
                        column: x => x.site_id,
                        principalTable: "sites",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "module_tasks",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    conversation_id = table.Column<Guid>(type: "uuid", nullable: false),
                    module_key = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    external_task_id = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    state = table.Column<string>(type: "text", nullable: false),
                    opened_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    closed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_step_kind = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    last_step_payload = table.Column<string>(type: "text", nullable: true),
                    last_step_actions = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_module_tasks", x => x.id);
                    table.ForeignKey(
                        name: "FK_module_tasks_conversations_conversation_id",
                        column: x => x.conversation_id,
                        principalTable: "conversations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_enabled_modules_site",
                table: "enabled_modules",
                column: "site_id");

            migrationBuilder.CreateIndex(
                name: "ux_module_tasks_conversation_active",
                table: "module_tasks",
                column: "conversation_id",
                unique: true,
                filter: "state = 'Open'");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "enabled_modules");

            migrationBuilder.DropTable(
                name: "module_tasks");
        }
    }
}
