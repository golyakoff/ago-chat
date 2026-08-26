using Ago.Chat.Infrastructure.Postgres.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Testcontainers.PostgreSql;

namespace Ago.Chat.Integration.Tests;

/// <summary>One Postgres container per test class collection, migrations applied once
/// (testing.md) - not per test, since Stage 1 has no truncation/reset story yet and every test
/// isolates itself with fresh ids instead.</summary>
public sealed class PostgresFixture : IAsyncLifetime
{
    private PostgreSqlContainer _container = null!;
    private IDisposable _dockerLock = null!;

    public NpgsqlDataSource DataSource { get; private set; } = null!;

    /// <summary>`16-05`: the container's connection string *with its password*, for the one test that
    /// exercises the production <c>AddPostgresPersistence(connectionString)</c> entry point rather
    /// than being handed a ready-made <see cref="NpgsqlDataSource"/>. Not simply
    /// <c>DataSource.ConnectionString</c>: Npgsql strips the password out of that property, so
    /// feeding it back in produces "No password has been provided but the backend requires one".</summary>
    public string ConnectionString { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        _dockerLock = await DockerResourceLock.AcquireAsync();

        _container = new PostgreSqlBuilder("postgres:17-alpine").Build();
        await _container.StartAsync();

        ConnectionString = _container.GetConnectionString();
        DataSource = new NpgsqlDataSourceBuilder(ConnectionString).Build();

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
}

[CollectionDefinition(Name)]
public sealed class PostgresCollection : ICollectionFixture<PostgresFixture>
{
    public const string Name = "Postgres";
}
