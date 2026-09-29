using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ago.Chat.Infrastructure.Postgres.Migrations
{
    /// <inheritdoc />
    /// <summary>
    /// `26-275`: seeds `admin-extra` (the extra-Administrator charge's own price catalog key,
    /// <see cref="Ago.Chat.Domain.SubscriptionTierBands.AdminExtraPriceKey"/>) at `ago-business` decision
    /// `0012`'s published figure, 1000 RUB/mo - the identical "a real charge site refuses cleanly when a
    /// key has no published version" reason `Stage25AddPricedResourceCatalog`
    /// (`seat-base`/`seat-extra`) and `Stage25AddDownloadOverageBilling` (`download-overage-per-gb`)
    /// already seed their own keys for. Without this, `PurchaseAdministratorSlotHandler` and the
    /// renewal charge this same item's own fix adds both return/throw
    /// <see cref="Ago.Chat.Domain.PriceCatalogErrors.PriceNotConfigured"/> on a fresh deployment that has
    /// never had anyone manually publish the key through the owner pricing screen.
    ///
    /// <para><b>Unlike those two migrations, this `Up` is written to be a no-op against a database that
    /// already has the key</b> - the live stand's own catalog already carries a manually-published
    /// `admin-extra` row (this item's own report), so this is not the "first ever price, no prior state
    /// to conflict with" case those two migrations were. Running this migration there must add nothing
    /// and change nothing, which is what `ON CONFLICT ... DO NOTHING` on both the key-uniqueness and the
    /// `(price_key, sequence)` uniqueness gives: a fresh deployment gets `v1` at 1000 RUB exactly like the
    /// two prior seeds; a deployment that already has the key keeps whatever is already published there,
    /// untouched.</para>
    /// </summary>
    public partial class Stage26SeedAdminExtraPrice : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                INSERT INTO priced_resources (id, price_key, last_sequence)
                VALUES ('9c5b1b7e-2a3a-4b7a-9b1a-000000000004', 'admin-extra', 1)
                ON CONFLICT (price_key) DO NOTHING;

                INSERT INTO published_price_versions (id, priced_resource_id, price_key, sequence, version, amount_rub, published_at)
                SELECT '9c5b1b7e-2a3a-4b7a-9b1a-000000000014', pr.id, 'admin-extra', 1, 'v1', 1000.00, '2026-09-29T00:00:00+00:00'
                FROM priced_resources pr
                WHERE pr.price_key = 'admin-extra'
                ON CONFLICT (price_key, sequence) DO NOTHING;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DELETE FROM published_price_versions WHERE price_key = 'admin-extra';
                DELETE FROM priced_resources WHERE price_key = 'admin-extra';
                """);
        }
    }
}
