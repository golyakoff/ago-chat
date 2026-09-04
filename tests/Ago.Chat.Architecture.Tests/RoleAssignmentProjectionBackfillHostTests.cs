using NetArchTest.Rules;

namespace Ago.Chat.Architecture.Tests;

/// <summary>
/// `22-16`: the same pair of rules <c>SchemaMigrationTests.TheMigrator_ReferencesPersistenceAndNothingAboveIt</c>
/// and <c>.TheMigrator_DoesApplyMigrations</c> already hold `Ago.Chat.Migrator` to, held here against
/// `Ago.Chat.RoleAssignmentBackfill` for the identical reason: a one-shot tool's whole safety argument
/// is "it cannot depend on the broker/cache/Keycloak being up", and that argument is only as good as a
/// test that fails the moment somebody adds a <c>PackageReference</c> that quietly reintroduces one.
/// </summary>
public class RoleAssignmentProjectionBackfillHostTests
{
    private const string BackfillType = "Ago.Chat.Infrastructure.Postgres.Backfill.RoleAssignmentProjectionBackfill";

    /// <summary>`22-16`'s own report: "it references Ago.Chat.Infrastructure.Postgres and nothing above
    /// it" - the identical csproj-comment-plus-test shape `adr/0056` established for the migrator, and
    /// the identical reason: `Ago.Chat.Module` validates RabbitMQ/Redis/S3/Keycloak at startup, and none
    /// of those are needed to stage an outbox row.</summary>
    [Fact]
    public void TheBackfillHost_ReferencesPersistenceAndNothingAboveIt()
    {
        string[] forbidden =
            ["Ago.Chat.Module", "Ago.Chat.Api", "Ago.Chat.Worker", "Ago.Chat.Webhooks", "Ago.Chat.Migrator"];

        var offenders = TestAssemblies.RoleAssignmentBackfill.Cecil.MainModule.AssemblyReferences
            .Select(reference => reference.Name)
            .Where(name => forbidden.Contains(name))
            .ToList();

        Assert.True(offenders.Count == 0,
            $"Ago.Chat.RoleAssignmentBackfill references {string.Join(", ", offenders)} - `22-16` confines "
            + "it to Ago.Chat.Infrastructure.Postgres and below, the same boundary adr/0056 draws for "
            + "Ago.Chat.Migrator and for the identical reason.");
    }

    /// <summary>The positive half - without this, deleting <c>RoleAssignmentProjectionBackfill</c>
    /// outright would make the rule above pass trivially, the same "a ban must not outlive the thing it
    /// protects" concern <c>SchemaMigrationTests.TheMigrator_DoesApplyMigrations</c> already states.
    /// </summary>
    [Fact]
    public void TheBackfillHost_DoesRunTheBackfill()
    {
        Types.InAssembly(TestAssemblies.RoleAssignmentBackfill.Reflection)
            .That().HaveNameEndingWith("Runner")
            .Should()
            .HaveDependencyOn(BackfillType)
            .GetResult()
            .ShouldPass("Ago.Chat.RoleAssignmentBackfill must be the host that runs RoleAssignmentProjectionBackfill");
    }
}
