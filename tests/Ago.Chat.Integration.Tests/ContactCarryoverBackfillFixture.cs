using Ago.Chat.Infrastructure.Postgres.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Testcontainers.PostgreSql;

namespace Ago.Chat.Integration.Tests;

/// <summary>
/// `23-59`: a dedicated container rather than the shared <see cref="PostgresFixture"/>/<see cref="PostgresCollection"/>
/// - the identical reason <c>RoleAssignmentProjectionBackfillFixture</c>'s own remarks give for its own
/// dedicated container. <see cref="Backfill.ContactCarryoverBackfill.ListPendingSiteIdsAsync"/> is
/// deliberately unscoped - it lists every site with an incomplete request in the whole database, because
/// that is <c>ContactCarryoverJob.SweepAsync</c>'s own real candidate list. Sharing a container with
/// every other test class (whose seeded requests would then be candidates too) is not a performance
/// shortcut but a correctness bug for that one method - <see cref="ResetAsync"/> truncates the handful of
/// tables this backfill reads and writes between tests, giving each <c>[Fact]</c> the empty-except-what-
/// it-seeded database its own assertions on the pending list depend on.
/// </summary>
public sealed class ContactCarryoverBackfillFixture : IAsyncLifetime
{
    private PostgreSqlContainer _container = null!;
    private IDisposable _dockerLock = null!;

    public NpgsqlDataSource DataSource { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        _dockerLock = await DockerResourceLock.AcquireAsync();

        _container = new PostgreSqlBuilder("postgres:17-alpine").Build();
        await _container.StartAsync();

        DataSource = new NpgsqlDataSourceBuilder(_container.GetConnectionString()).Build();

        await using var db = CreateDbContext();
        await db.Database.MigrateAsync();
    }

    public async Task DisposeAsync()
    {
        await DataSource.DisposeAsync();
        await _container.DisposeAsync();
        _dockerLock.Dispose();
    }

    public AgoChatDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<AgoChatDbContext>().UseNpgsql(DataSource).Options;
        return new AgoChatDbContext(options);
    }

    /// <summary>Called at the top of every <c>[Fact]</c> that uses this fixture, not once per class -
    /// the container is shared across every test method in the collection, so without this a test that
    /// runs after another would see that other test's sites/requests as its own candidates.</summary>
    public async Task ResetAsync()
    {
        await using var db = CreateDbContext();
        await db.Database.ExecuteSqlRawAsync(
            "TRUNCATE TABLE outbox, contact_carryover_requests, visitor_contact_details, visitors, sites RESTART IDENTITY CASCADE;");
    }
}

[CollectionDefinition(Name)]
public sealed class ContactCarryoverBackfillCollection : ICollectionFixture<ContactCarryoverBackfillFixture>
{
    public const string Name = "ContactCarryoverBackfill";
}
