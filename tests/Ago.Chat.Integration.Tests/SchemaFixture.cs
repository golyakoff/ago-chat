using Ago.Chat.Infrastructure.Postgres.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Testcontainers.PostgreSql;

namespace Ago.Chat.Integration.Tests;

/// <summary>
/// `8-08`: one Postgres container for every schema-migration test, deliberately <b>not</b> migrated on
/// startup - unlike <see cref="PostgresFixture"/>, whose whole job is to hand a test a ready schema.
/// Here the schema's state is the thing under test, so each test asks for the state it needs through
/// <see cref="ResetToAsync"/>.
///
/// <para>One container rather than one per test: <see cref="DockerResourceLock"/> serialises container
/// fleets machine-wide, so a fixture per test would multiply the slowest part of this suite by the
/// number of tests. Resetting is a <c>DROP SCHEMA public CASCADE</c>, which is milliseconds and leaves
/// nothing behind - including <c>__EFMigrationsHistory</c>, which is the row set that actually
/// matters here.</para>
/// </summary>
public sealed class SchemaFixture : IAsyncLifetime
{
    private PostgreSqlContainer _container = null!;
    private IDisposable _dockerLock = null!;

    public string ConnectionString { get; private set; } = null!;

    /// <summary>Every migration compiled into <c>Ago.Chat.Infrastructure.Postgres</c>, oldest first -
    /// the same list <c>SchemaVersionCheck</c> compares against, read once here so a test can name
    /// "the one before last" without hard-coding a migration id that a later item would invalidate.</summary>
    public IReadOnlyList<string> AllMigrations { get; private set; } = [];

    public async Task InitializeAsync()
    {
        _dockerLock = await DockerResourceLock.AcquireAsync();

        _container = new PostgreSqlBuilder("postgres:17-alpine").Build();
        await _container.StartAsync();
        ConnectionString = _container.GetConnectionString();

        await using var db = CreateDbContext();
        AllMigrations = db.Database.GetMigrations().ToList();
    }

    public async Task DisposeAsync()
    {
        await _container.DisposeAsync();
        _dockerLock.Dispose();
    }

    public AgoChatDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<AgoChatDbContext>().UseNpgsql(ConnectionString).Options;
        return new AgoChatDbContext(options);
    }

    /// <summary>
    /// Empties the database and then migrates forward to <paramref name="targetMigration"/>.
    ///
    /// <para><see langword="null"/> leaves it empty (nothing applied). A migration id migrates
    /// <em>forward</em> to exactly that point - never backward, so no <c>Down()</c> is ever executed
    /// here. That matters beyond tidiness: `adr/0056` is explicit that this project has never run a
    /// <c>Down()</c> and will not start now, and <c>Stage2PartitionMessages.Down</c> throws outright.
    /// Building an out-of-date database by going forward-and-stopping is the only honest way to make
    /// one.</para>
    /// </summary>
    public async Task ResetToAsync(string? targetMigration)
    {
        await using (var connection = new NpgsqlConnection(ConnectionString))
        {
            await connection.OpenAsync();
            await using var drop = new NpgsqlCommand(
                "drop schema public cascade; create schema public;", connection);
            await drop.ExecuteNonQueryAsync();
        }

        if (targetMigration is null)
        {
            return;
        }

        await using var db = CreateDbContext();
        var migrator = db.GetInfrastructure().GetRequiredService<IMigrator>();
        await migrator.MigrateAsync(targetMigration);
    }

    /// <summary>The state the 2026-08-25 incident actually produced: a database that is real, populated
    /// and behind. One migration behind rather than three, because "behind by any amount" is the same
    /// condition and one is the harder case to detect.</summary>
    public Task ResetToOneMigrationBehindAsync() => ResetToAsync(AllMigrations[^2]);

    public Task ResetToCurrentAsync() => ResetToAsync(AllMigrations[^1]);
}

[CollectionDefinition(Name)]
public sealed class SchemaCollection : ICollectionFixture<SchemaFixture>
{
    public const string Name = "Schema";
}
