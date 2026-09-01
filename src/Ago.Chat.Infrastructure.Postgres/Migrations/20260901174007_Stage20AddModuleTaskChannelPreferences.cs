using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ago.Chat.Infrastructure.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class Stage20AddModuleTaskChannelPreferences : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "module_task_channel_preferences",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    site_id = table.Column<Guid>(type: "uuid", nullable: false),
                    module_task_id = table.Column<Guid>(type: "uuid", nullable: false),
                    visitor_id = table.Column<Guid>(type: "uuid", nullable: false),
                    channel_identity_id = table.Column<Guid>(type: "uuid", nullable: false),
                    priority = table.Column<int>(type: "integer", nullable: false),
                    added_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_module_task_channel_preferences", x => x.id);
                    table.ForeignKey(
                        name: "FK_module_task_channel_preferences_channel_identities_channel_~",
                        column: x => x.channel_identity_id,
                        principalTable: "channel_identities",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_module_task_channel_preferences_module_tasks_module_task_id",
                        column: x => x.module_task_id,
                        principalTable: "module_tasks",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_module_task_channel_preferences_sites_site_id",
                        column: x => x.site_id,
                        principalTable: "sites",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_module_task_channel_preferences_visitors_visitor_id",
                        column: x => x.visitor_id,
                        principalTable: "visitors",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_module_task_channel_preferences_channel_identity_id",
                table: "module_task_channel_preferences",
                column: "channel_identity_id");

            migrationBuilder.CreateIndex(
                name: "IX_module_task_channel_preferences_site_id",
                table: "module_task_channel_preferences",
                column: "site_id");

            migrationBuilder.CreateIndex(
                name: "IX_module_task_channel_preferences_visitor_id",
                table: "module_task_channel_preferences",
                column: "visitor_id");

            migrationBuilder.CreateIndex(
                name: "ux_module_task_channel_preferences_module_task_channel_identity",
                table: "module_task_channel_preferences",
                columns: new[] { "module_task_id", "channel_identity_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_module_task_channel_preferences_module_task_priority",
                table: "module_task_channel_preferences",
                columns: new[] { "module_task_id", "priority" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "module_task_channel_preferences");
        }
    }
}
