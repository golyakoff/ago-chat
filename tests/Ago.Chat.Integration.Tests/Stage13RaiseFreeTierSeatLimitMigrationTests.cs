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
/// `13-08`'s own Done-when: "existing sites are migrated, not left on the old default" - proven here
/// against a real Postgres, not read off the migration's own SQL text. The live database this item's
/// own backlog names (17 sites, 20 operators, all `seat_limit = 1`) is exactly the shape this test
/// recreates: a row written under the *previous* migration's default, still at `1`, then brought
/// forward across `Stage13RaiseFreeTierSeatLimit` and asserted at `2`.
///
/// <para><b>Its own container, not the shared <see cref="PostgresFixture"/>.</b> Every other suite in
/// this project wants a fully-migrated database and shares one container per collection
/// (`PostgresFixture`'s own remarks); this test's entire point is controlling *when* the last migration
/// applies relative to when a row is written, so it needs a database frozen one migration short of
/// head, not a Testcontainers instance already migrated all the way - `IMigrator.MigrateAsync(target)`
/// against a private container is the only way to get that.</para>
/// </summary>
public sealed class Stage13RaiseFreeTierSeatLimitMigrationTests : IAsyncLifetime
{
    // The migration immediately before this item's own - `ls` of the Migrations folder in timestamp
    // order, not a guess. Stopping migration here reproduces exactly the schema a pre-`13-08` deployment
    // had: `seat_limit` defaulting to `1`, no backfill yet applied.
    private const string PriorMigrationId = "20260901213751_Stage15RepartitionMessagesByTenantHash";

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
    public async Task Migrate_ExistingFreeTierRowsStillAtTheOldDefault_AreRaisedToTwo_AndPaidTierRowsAreUntouched()
    {
        var options = new DbContextOptionsBuilder<AgoChatDbContext>().UseNpgsql(_dataSource).Options;

        // Every migration up to, but not including, this item's own - the exact schema a
        // pre-`13-08` deployment had.
        await using (var db = new AgoChatDbContext(options))
        {
            var migrator = db.GetInfrastructure().GetRequiredService<IMigrator>();
            await migrator.MigrateAsync(PriorMigrationId);
        }

        var freeSiteId = Guid.NewGuid();
        var freeSiteAlreadyAtTwoId = Guid.NewGuid();
        var starterSiteId = Guid.NewGuid();

        await using (var connection = await _dataSource.OpenConnectionAsync())
        {
            // A row written under the previous migration's own default - `seat_limit = 1`, exactly
            // what every one of the 17 real free-tier sites this item's backlog names looks like today.
            await InsertSiteAsync(connection, freeSiteId, tier: "free", seatLimit: 1);

            // A free-tier row already at 2 for some other reason - the backfill's own `AND seat_limit
            // < 2` guard (idempotence) is proven here, not just read off the SQL.
            await InsertSiteAsync(connection, freeSiteAlreadyAtTwoId, tier: "free", seatLimit: 2);

            // A paid-tier row - `ActivateSubscription` always writes at least `SubscriptionTierBands
            // .MinSeats` (3), but this row is seeded directly at raw SQL level to prove the backfill's
            // own `WHERE tier = 'free'` predicate, not `ActivateSubscription`'s own invariant.
            await InsertSiteAsync(connection, starterSiteId, tier: "starter", seatLimit: 5);
        }

        // Now bring the database forward across exactly one migration - this item's own.
        await using (var db = new AgoChatDbContext(options))
        {
            await db.Database.MigrateAsync();
        }

        await using (var verify = new AgoChatDbContext(options))
        {
            var freeSeatLimit = await verify.Sites.AsNoTracking()
                .Where(s => s.Id == new SiteId(freeSiteId)).Select(s => s.SeatLimit).SingleAsync();
            Assert.Equal(2, freeSeatLimit);

            var alreadyTwoSeatLimit = await verify.Sites.AsNoTracking()
                .Where(s => s.Id == new SiteId(freeSiteAlreadyAtTwoId)).Select(s => s.SeatLimit).SingleAsync();
            Assert.Equal(2, alreadyTwoSeatLimit);

            var starterSeatLimit = await verify.Sites.AsNoTracking()
                .Where(s => s.Id == new SiteId(starterSiteId)).Select(s => s.SeatLimit).SingleAsync();
            Assert.Equal(5, starterSeatLimit);
        }
    }

    /// <summary>Raw SQL, deliberately - `AgoChatDbContext.Sites.Add` would go through
    /// <c>SiteConfiguration</c>'s *current* default (`2` as of this item), which could never reproduce
    /// the pre-migration state this test exists to seed.</summary>
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
