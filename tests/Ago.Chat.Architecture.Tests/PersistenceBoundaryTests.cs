namespace Ago.Chat.Architecture.Tests;

/// <summary>
/// adr/0004, clean-architecture.md: one project per external technology, so swapping Postgres for
/// something else (Stage 9: MySQL) never means chasing a stray reference through Module or a host.
/// Domain/Application already can't see Npgsql (ForbiddenTypeTests, the dependency rule) - this rule
/// closes the other direction: nothing *outside* Infrastructure.Postgres, including Module and the
/// hosts, may reference it directly either.
/// </summary>
public class PersistenceBoundaryTests
{
    [Fact]
    public void OnlyInfrastructurePostgres_ReferencesNpgsqlOrEntityFrameworkCoreDirectly()
    {
        var offenders = new List<string>();

        foreach (var assembly in TestAssemblies.AllProduct)
        {
            if (assembly.Name == "Ago.Chat.Infrastructure.Postgres")
            {
                continue;
            }

            var direct = assembly.Cecil.MainModule.AssemblyReferences
                .Where(r => r.Name is "Npgsql" or "Microsoft.EntityFrameworkCore" or "Microsoft.EntityFrameworkCore.Relational")
                .Select(r => r.Name);

            offenders.AddRange(direct.Select(name => $"{assembly.Name} -> {name}"));
        }

        Assert.True(offenders.Count == 0,
            $"Only Ago.Chat.Infrastructure.Postgres may reference Npgsql/EF Core directly: {string.Join(", ", offenders)}");
    }
}
