using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ago.Chat.Infrastructure.Postgres.Migrations
{
    /// <summary>
    /// `14-02`/`adr/0069`: one new table - which shop's bot token AGO holds for which site and
    /// channel (<c>Ago.Chat.Domain.ChannelCredential</c>).
    ///
    /// <para>Additive and fully reversible: a new table with one foreign key and no change to any
    /// existing column, so <c>Down</c> is a single <c>DROP TABLE</c>. Nothing to backfill - no
    /// existing site has ever held a channel credential.</para>
    ///
    /// <para><c>ux_channel_credentials_site_kind_active</c> is a <b>partial</b> unique index (the
    /// <c>active</c> filter), not a plain one on <c>(site_id, kind)</c> - see
    /// <c>ChannelCredentialConfiguration</c>'s own remarks for why: a revoked credential must never
    /// block registering its replacement.</para>
    /// </summary>
    public partial class Stage14AddChannelCredentials : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "channel_credentials",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    site_id = table.Column<Guid>(type: "uuid", nullable: false),
                    kind = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    token_ciphertext = table.Column<byte[]>(type: "bytea", nullable: false),
                    webhook_secret_hash = table.Column<byte[]>(type: "bytea", nullable: false),
                    active = table.Column<bool>(type: "boolean", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_channel_credentials", x => x.id);
                    table.ForeignKey(
                        name: "FK_channel_credentials_sites_site_id",
                        column: x => x.site_id,
                        principalTable: "sites",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ux_channel_credentials_site_kind_active",
                table: "channel_credentials",
                columns: new[] { "site_id", "kind" },
                unique: true,
                filter: "active");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "channel_credentials");
        }
    }
}
