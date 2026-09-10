using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ago.Chat.Infrastructure.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class Stage25AdministratorPurchasePath : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_role_change_records_operators_changed_by_operator_id",
                table: "role_change_records");

            migrationBuilder.AlterColumn<Guid>(
                name: "changed_by_operator_id",
                table: "role_change_records",
                type: "uuid",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "uuid");

            migrationBuilder.AddColumn<int>(
                name: "admin_extra_price_version",
                table: "billing_subscriptions",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "extra_administrators_purchased",
                table: "billing_subscriptions",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddForeignKey(
                name: "FK_role_change_records_operators_changed_by_operator_id",
                table: "role_change_records",
                column: "changed_by_operator_id",
                principalTable: "operators",
                principalColumn: "id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_role_change_records_operators_changed_by_operator_id",
                table: "role_change_records");

            migrationBuilder.DropColumn(
                name: "admin_extra_price_version",
                table: "billing_subscriptions");

            migrationBuilder.DropColumn(
                name: "extra_administrators_purchased",
                table: "billing_subscriptions");

            migrationBuilder.AlterColumn<Guid>(
                name: "changed_by_operator_id",
                table: "role_change_records",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"),
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldNullable: true);

            migrationBuilder.AddForeignKey(
                name: "FK_role_change_records_operators_changed_by_operator_id",
                table: "role_change_records",
                column: "changed_by_operator_id",
                principalTable: "operators",
                principalColumn: "id",
                onDelete: ReferentialAction.Cascade);
        }
    }
}
