using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ago.Chat.Infrastructure.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class Stage23AddUnconditionalModuleQuantityGrant : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "unconditional_grant_reason",
                table: "module_quantity_grants",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "unconditional_grant_set_at",
                table: "module_quantity_grants",
                type: "timestamptz",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "unconditional_grant_set_by",
                table: "module_quantity_grants",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "unconditionally_granted_by_owner",
                table: "module_quantity_grants",
                type: "boolean",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "unconditional_grant_reason",
                table: "module_quantity_grants");

            migrationBuilder.DropColumn(
                name: "unconditional_grant_set_at",
                table: "module_quantity_grants");

            migrationBuilder.DropColumn(
                name: "unconditional_grant_set_by",
                table: "module_quantity_grants");

            migrationBuilder.DropColumn(
                name: "unconditionally_granted_by_owner",
                table: "module_quantity_grants");
        }
    }
}
