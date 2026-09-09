using Ago.Chat.Domain;
using Ago.Chat.Infrastructure.Postgres.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Testcontainers.PostgreSql;

namespace Ago.Chat.Integration.Tests;

/// <summary>
/// `25-25`'s own analogue of `Stage13RaiseFreeTierSeatLimitMigrationTests` - the identical "freeze the
/// schema one migration short of head, write a row under the *previous* migration's default, then
/// bring it forward across exactly this item's own migration" shape, proven against a real Postgres
/// rather than read off the migration's own SQL text.
///
/// <para><b>Its own container, not the shared <see cref="PostgresFixture"/></b> - the identical reason
/// that test's own remarks give: this test's entire point is controlling *when* the last migration
/// applies relative to when a row is written.</para>
/// </summary>
public sealed class Stage25AddSiteAdminLimitMigrationTests : IAsyncLifetime
{
    // The migration immediately before this item's own - `ls` of the Migrations folder in timestamp
    // order, not a guess. Stopping migration here reproduces exactly the schema a pre-`25-25`
    // deployment had: no `admin_limit` column at all.
    private const string PriorMigrationId = "20260909161335_Stage25AddSiteWidgetAcceptUnverifiedPhone";

    private PostgreSqlContainer _container = null!;
    private IDisposable _dockerLock = null!;
    private NpgsqlDataSource _dataSource = null!;

    public async Task InitializeAsync()
    {
        _dockerLock = await DockerResourceLock.AcquireAsync();
        _container = new PostgreSqlBuilder("postgres:17-alpine").Build();
        await _container.StartAsync();
        _dataSource = new NpgsqlDataSourceBuilder(_container.GetConnectionString()).Build();
    }

    public async Task DisposeAsync()
    {
        await _dataSource.DisposeAsync();
        await _container.DisposeAsync();
        _dockerLock.Dispose();
    }

    [Fact]
    public async Task Migrate_ExistingPaidTierRowsAtTheFreeTierDefault_AreRaisedToTwo_AndFreeTierRowsAreUntouched()
    {
        var options = new DbContextOptionsBuilder<AgoChatDbContext>().UseNpgsql(_dataSource).Options;

        // Every migration up to, but not including, this item's own - the exact schema a pre-`25-25`
        // deployment had: `sites` has no `admin_limit` column yet.
        await using (var db = new AgoChatDbContext(options))
        {
            var migrator = db.GetInfrastructure().GetRequiredService<IMigrator>();
            await migrator.MigrateAsync(PriorMigrationId);
        }

        var freeSiteId = Guid.NewGuid();
        var starterSiteId = Guid.NewGuid();
        var growthSiteId = Guid.NewGuid();

        await using (var connection = await _dataSource.OpenConnectionAsync())
        {
            // A free-tier row, written before this migration ever existed - no `admin_limit` column to
            // seed at all yet, exactly what every real pre-`25-25` free-tier row looks like.
            await InsertSiteAsync(connection, freeSiteId, tier: "free", seatLimit: 2);

            // Two paid-tier rows, on both bands `SubscriptionTierBands` splits Business into - proving
            // the backfill's own `WHERE tier <> 'free'` predicate reaches both, not just one.
            await InsertSiteAsync(connection, starterSiteId, tier: "starter", seatLimit: 5);
            await InsertSiteAsync(connection, growthSiteId, tier: "growth", seatLimit: 25);
        }

        // Now bring the database forward across exactly one migration - this item's own.
        await using (var db = new AgoChatDbContext(options))
        {
            await db.Database.MigrateAsync();
        }

        await using (var verify = new AgoChatDbContext(options))
        {
            var freeAdminLimit = await verify.Sites.AsNoTracking()
                .Where(s => s.Id == new SiteId(freeSiteId)).Select(s => s.AdminLimit).SingleAsync();
            Assert.Equal(SubscriptionTierBands.FreeAdminsIncluded, freeAdminLimit);

            var starterAdminLimit = await verify.Sites.AsNoTracking()
                .Where(s => s.Id == new SiteId(starterSiteId)).Select(s => s.AdminLimit).SingleAsync();
            Assert.Equal(SubscriptionTierBands.BusinessAdminsIncluded, starterAdminLimit);

            var growthAdminLimit = await verify.Sites.AsNoTracking()
                .Where(s => s.Id == new SiteId(growthSiteId)).Select(s => s.AdminLimit).SingleAsync();
            Assert.Equal(SubscriptionTierBands.BusinessAdminsIncluded, growthAdminLimit);
        }
    }

    [Fact]
    public async Task Migrate_APaidTierRowAlreadyAtTwoForSomeOtherReason_IsLeftUntouched()
    {
        var options = new DbContextOptionsBuilder<AgoChatDbContext>().UseNpgsql(_dataSource).Options;

        await using (var db = new AgoChatDbContext(options))
        {
            var migrator = db.GetInfrastructure().GetRequiredService<IMigrator>();
            await migrator.MigrateAsync(PriorMigrationId);
        }

        var starterSiteId = Guid.NewGuid();
        await using (var connection = await _dataSource.OpenConnectionAsync())
        {
            await InsertSiteAsync(connection, starterSiteId, tier: "starter", seatLimit: 5);
        }

        await using (var db = new AgoChatDbContext(options))
        {
            await db.Database.MigrateAsync();
        }

        // The backfill's own `AND admin_limit < 2` guard (idempotence) - a rerun of the identical
        // migration on an already-migrated row must not fail or change anything, proven by running
        // MigrateAsync a second time against a database already at head.
        await using (var db = new AgoChatDbContext(options))
        {
            await db.Database.MigrateAsync();
        }

        await using (var verify = new AgoChatDbContext(options))
        {
            var starterAdminLimit = await verify.Sites.AsNoTracking()
                .Where(s => s.Id == new SiteId(starterSiteId)).Select(s => s.AdminLimit).SingleAsync();
            Assert.Equal(2, starterAdminLimit);
        }
    }

    /// <summary>Raw SQL, deliberately - `AgoChatDbContext.Sites.Add` would go through
    /// <see cref="SiteConfiguration"/>'s *current* default (`admin_limit` included), which could never
    /// reproduce the pre-migration state this test exists to seed.</summary>
    private static async Task InsertSiteAsync(NpgsqlConnection connection, Guid id, string tier, int seatLimit)
    {
        await using var command = new NpgsqlCommand(
            "insert into sites (id, public_key, allowed_origins, tier, seat_limit) values (@id, @publicKey, '{}', @tier, @seatLimit)",
            connection);
        command.Parameters.AddWithValue("id", id);
        command.Parameters.AddWithValue("publicKey", $"site_{id:N}");
        command.Parameters.AddWithValue("tier", tier);
        command.Parameters.AddWithValue("seatLimit", seatLimit);
        await command.ExecuteNonQueryAsync();
    }
}
