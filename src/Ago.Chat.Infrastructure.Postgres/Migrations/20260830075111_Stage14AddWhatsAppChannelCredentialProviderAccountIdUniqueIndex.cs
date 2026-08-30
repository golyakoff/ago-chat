using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ago.Chat.Infrastructure.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class Stage14AddWhatsAppChannelCredentialProviderAccountIdUniqueIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "ux_channel_credentials_kind_provideraccountid_active",
                table: "channel_credentials",
                columns: new[] { "kind", "provider_account_id" },
                unique: true,
                filter: "active AND provider_account_id IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ux_channel_credentials_kind_provideraccountid_active",
                table: "channel_credentials");
        }
    }
}
