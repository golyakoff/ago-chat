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

    public NpgsqlDataSource DataSource { get; private set; } = null!;

    public async Task InitializeAsync()
    {
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
