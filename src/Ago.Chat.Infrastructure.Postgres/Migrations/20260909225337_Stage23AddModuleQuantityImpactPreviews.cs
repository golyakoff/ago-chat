using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ago.Chat.Infrastructure.Postgres.Migrations
{
    /// <summary>`23-88`: one row per (site, module) - "how many of module K's own things would
    /// candidate quantity Q exceed" - see <see cref="Persistence.ModuleQuantityImpactPreviewConfiguration"/>
    /// and <see cref="Domain.ModuleQuantityImpactPreview"/>'s own remarks for the full shape. No data
    /// backfill: a freshly added table with nothing to have held a value before this migration
    /// ran.</summary>
    public partial class Stage23AddModuleQuantityImpactPreviews : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "module_quantity_impact_previews",
                columns: table => new
                {
                    site_id = table.Column<Guid>(type: "uuid", nullable: false),
                    module_key = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    requested_quantity = table.Column<int>(type: "integer", nullable: false),
                    requested_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    affected_count = table.Column<int>(type: "integer", nullable: true),
                    affected_item_display_names = table.Column<string>(type: "text", nullable: false),
                    answered_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_module_quantity_impact_previews", x => new { x.site_id, x.module_key });
                    table.ForeignKey(
                        name: "FK_module_quantity_impact_previews_sites_site_id",
                        column: x => x.site_id,
                        principalTable: "sites",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "module_quantity_impact_previews");
        }
    }
}
