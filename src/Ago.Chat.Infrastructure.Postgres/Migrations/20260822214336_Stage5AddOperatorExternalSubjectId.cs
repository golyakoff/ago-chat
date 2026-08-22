using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ago.Chat.Infrastructure.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class Stage5AddOperatorExternalSubjectId : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "external_subject_id",
                table: "operators",
                type: "text",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_operators_external_subject_id",
                table: "operators",
                column: "external_subject_id",
                unique: true,
                filter: "external_subject_id IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_operators_external_subject_id",
                table: "operators");

            migrationBuilder.DropColumn(
                name: "external_subject_id",
                table: "operators");
        }
    }
}
