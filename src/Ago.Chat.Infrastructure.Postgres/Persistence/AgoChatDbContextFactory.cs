using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Ago.Chat.Infrastructure.Postgres.Persistence;

/// <summary>
/// Design-time only - `dotnet ef migrations add`/`database update` need a way to construct
/// <see cref="AgoChatDbContext"/> without a running host's DI container. `migrations add` never
/// actually connects (schema generation is static from the model, not the database), so any
/// syntactically valid connection string satisfies it; `database update` needs a real, reachable
/// one. Either way the value comes from an environment variable, never a literal here - even a
/// throwaway local placeholder is a credential shape this repository does not commit
/// (repositories.md - "no secrets, ever").
/// </summary>
public sealed class AgoChatDbContextFactory : IDesignTimeDbContextFactory<AgoChatDbContext>
{
    public AgoChatDbContext CreateDbContext(string[] args)
    {
        var connectionString = Environment.GetEnvironmentVariable("AGO_CHAT_CONNECTION_STRING")
            ?? throw new InvalidOperationException(
                "Set AGO_CHAT_CONNECTION_STRING before running dotnet ef - e.g. the docker-compose " +
                "Postgres from local-dev.md: Host=localhost;Port=5432;Database=ago_chat;Username=...;Password=...");

        var optionsBuilder = new DbContextOptionsBuilder<AgoChatDbContext>();
        optionsBuilder.UseNpgsql(connectionString);
        return new AgoChatDbContext(optionsBuilder.Options);
    }
}
