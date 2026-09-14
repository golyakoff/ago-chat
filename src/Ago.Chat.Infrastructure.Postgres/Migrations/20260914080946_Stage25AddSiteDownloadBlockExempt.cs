using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ago.Chat.Infrastructure.Postgres.Migrations
{
    /// <inheritdoc />
    /// <summary>`25-83`: the platform owner's own free, indefinite bypass of the hard download-block
    /// threshold - `Site.DownloadBlockExempt`'s own remarks. A plain, non-nullable boolean column with
    /// a `false` default - every row that predates this migration reads back "not exempt", no
    /// backfill needed.</summary>
    public partial class Stage25AddSiteDownloadBlockExempt : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "download_block_exempt",
                table: "sites",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            // The last owner who set the flag above, and why - Site.DownloadBlockExemptionChangedBy's
            // own remarks. All three null for every row this migration touches; nothing has changed
            // the flag yet.
            migrationBuilder.AddColumn<string>(
                name: "download_block_exemption_changed_by",
                table: "sites",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "download_block_exemption_reason",
                table: "sites",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "download_block_exemption_changed_at",
                table: "sites",
                type: "timestamp with time zone",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "download_block_exempt",
                table: "sites");

            migrationBuilder.DropColumn(
                name: "download_block_exemption_changed_by",
                table: "sites");

            migrationBuilder.DropColumn(
                name: "download_block_exemption_reason",
                table: "sites");

            migrationBuilder.DropColumn(
                name: "download_block_exemption_changed_at",
                table: "sites");
        }
    }
}
