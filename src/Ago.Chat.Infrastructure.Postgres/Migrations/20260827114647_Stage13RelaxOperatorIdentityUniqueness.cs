using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ago.Chat.Infrastructure.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class Stage13RelaxOperatorIdentityUniqueness : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_operators_external_subject_id",
                table: "operators");

            migrationBuilder.CreateIndex(
                name: "IX_operators_external_subject_id_site_id",
                table: "operators",
                columns: new[] { "external_subject_id", "site_id" },
                unique: true,
                filter: "external_subject_id IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_operators_external_subject_id_site_id",
                table: "operators");

            migrationBuilder.CreateIndex(
                name: "IX_operators_external_subject_id",
                table: "operators",
                column: "external_subject_id",
                unique: true,
                filter: "external_subject_id IS NOT NULL");
        }
    }
}
