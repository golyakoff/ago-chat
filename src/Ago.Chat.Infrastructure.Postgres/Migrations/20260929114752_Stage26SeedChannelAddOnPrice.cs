using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ago.Chat.Infrastructure.Postgres.Migrations
{
    /// <inheritdoc />
    /// <summary>
    /// `26-278`: seeds `channel-addon` (the connected-channel add-on's own price catalog key,
    /// <see cref="Ago.Chat.Domain.ChannelAddOnPricing.ChannelAddOnKey"/>) at `ago-business` decision
    /// `0012`'s published figure, 100 RUB/mo - the identical "a real charge site refuses cleanly when a
    /// key has no published version" reason `Stage25AddPricedResourceCatalog` (`seat-base`/`seat-extra`),
    /// `Stage25AddDownloadOverageBilling` (`download-overage-per-gb`) and `Stage26SeedAdminExtraPrice`
    /// (`admin-extra`) already seed their own keys for. Without this, `PurchaseChannelAddOnHandler` and
    /// the renewal charge this same item's own fix adds both return/throw
    /// <see cref="Ago.Chat.Domain.PriceCatalogErrors.PriceNotConfigured"/> on a fresh deployment that has
    /// never had anyone manually publish the key through the owner pricing screen.
    ///
    /// <para><b>Sequenced after <c>Stage26SeedAdminExtraPrice</c> (`26-277`), as CLAUDE.md rule 13's
    /// migration lane requires</b> - this migration's own timestamp (`20260929114752`) sorts after that
    /// one's (`20260929101914`).</para>
    ///
    /// <para><b>A no-op against a database that already has the key</b>, the identical
    /// <c>Stage26SeedAdminExtraPrice</c> reasoning restated: the live stand's own catalog already carries
    /// a manually-published `channel-addon` row (`docs/backlog/26-278-*.md`'s own "the stand has it
    /// published at 100" finding), so this is not the "first ever price, no prior state to conflict with"
    /// case `Stage25AddPricedResourceCatalog`/`Stage25AddDownloadOverageBilling` were. `ON CONFLICT ...
    /// DO NOTHING` on both the key-uniqueness and the `(price_key, sequence)` uniqueness gives: a fresh
    /// deployment gets `v1` at 100 RUB exactly like the three prior seeds; a deployment that already has
    /// the key keeps whatever is already published there, untouched.</para>
    /// </summary>
    public partial class Stage26SeedChannelAddOnPrice : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                INSERT INTO priced_resources (id, price_key, last_sequence)
                VALUES ('9c5b1b7e-2a3a-4b7a-9b1a-000000000005', 'channel-addon', 1)
                ON CONFLICT (price_key) DO NOTHING;

                INSERT INTO published_price_versions (id, priced_resource_id, price_key, sequence, version, amount_rub, published_at)
                SELECT '9c5b1b7e-2a3a-4b7a-9b1a-000000000015', pr.id, 'channel-addon', 1, 'v1', 100.00, '2026-09-29T00:00:00+00:00'
                FROM priced_resources pr
                WHERE pr.price_key = 'channel-addon'
                ON CONFLICT (price_key, sequence) DO NOTHING;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DELETE FROM published_price_versions WHERE price_key = 'channel-addon';
                DELETE FROM priced_resources WHERE price_key = 'channel-addon';
                """);
        }
    }
}
