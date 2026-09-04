using Ago.Chat.Infrastructure.Postgres.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Testcontainers.PostgreSql;

namespace Ago.Chat.Integration.Tests;

/// <summary>
/// `22-16`: a dedicated container rather than the shared <see cref="PostgresFixture"/>/<see cref="PostgresCollection"/>
/// every other test in this project defaults to (`OutboxDispatcherFixture`'s own precedent for "this one
/// needs its own database"). <see cref="Backfill.RoleAssignmentProjectionBackfill.RunAsync"/> is
/// deliberately unscoped - it processes <i>every</i> candidate operator in the database, because that is
/// its actual job against the real one. That makes it the one type in this test suite for which sharing
/// a container with every other test class (whose seeded operators would then be candidates too) is not
/// a performance shortcut but a correctness bug: <see cref="ResetAsync"/> truncates the handful of
/// tables this backfill reads between tests, giving each <c>[Fact]</c> in
/// <c>RoleAssignmentProjectionBackfillTests</c> the empty-except-what-it-seeded database its own
/// assertions on <c>CandidatesConsidered</c>/<c>Published.Count</c> depend on, without paying for a
/// fresh container per test.
/// </summary>
public sealed class RoleAssignmentProjectionBackfillFixture : IAsyncLifetime
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
    /// the container is shared across every test method (one per collection, not one per test, the
    /// same trade this project's every other fixture makes), so without this a test that runs after
    /// another would see that other test's operators as its own candidates.</summary>
    public async Task ResetAsync()
    {
        await using var db = CreateDbContext();
        await db.Database.ExecuteSqlRawAsync(
            "TRUNCATE TABLE outbox, operator_roles, roles, operators, sites RESTART IDENTITY CASCADE;");
    }
}

[CollectionDefinition(Name)]
public sealed class RoleAssignmentProjectionBackfillCollection : ICollectionFixture<RoleAssignmentProjectionBackfillFixture>
{
    public const string Name = "RoleAssignmentProjectionBackfill";
}
