using NetArchTest.Rules;

namespace Ago.Chat.Architecture.Tests;

/// <summary>
/// `8-08`'s last Done-when: "an architecture test keeps <c>Database.Migrate()</c> out of the three
/// serving hosts, so this cannot be quietly reintroduced at startup later."
///
/// <para>That last clause is the point. `adr/0056` rejects startup migration on three separate counts
/// - replicas racing the same migration, three hosts carrying a capability one of them needs, and
/// welding "may I serve traffic" to "may I change the schema" - but every one of those is an argument,
/// and an argument is exactly what a future session reaches past when a one-line
/// <c>app.Services.Migrate()</c> would fix its local problem. These rules are the argument made
/// mechanical.</para>
/// </summary>
public class SchemaMigrationTests
{
    private const string MigrateExtensions =
        "Microsoft.EntityFrameworkCore.RelationalDatabaseFacadeExtensions";

    private const string ApplierType = "Ago.Chat.Infrastructure.Postgres.Schema.SchemaMigrationApplier";

    /// <summary>
    /// The rule itself, read out of IL rather than from a type-dependency graph: <c>Migrate</c> and
    /// <c>MigrateAsync</c> are extension methods on <c>DatabaseFacade</c>, which every host legitimately
    /// touches (health checks, <c>GetAppliedMigrations</c>), so banning the *type* would ban the read
    /// half too. <see cref="IlMemberScanner"/> exists for exactly this distinction - it was built for
    /// `adr/0011`'s <c>Guid.NewGuid()</c> ban, which has the same shape.
    /// </summary>
    [Theory]
    [InlineData("Migrate")]
    [InlineData("MigrateAsync")]
    public void ServingHosts_NeverApplyMigrations(string member)
    {
        foreach (var host in TestAssemblies.ServingHosts)
        {
            var offenders = IlMemberScanner.FindCallers(host.Cecil, MigrateExtensions, member);

            Assert.True(offenders.Count == 0,
                $"{host.Name} calls {member}() - only Ago.Chat.Migrator may apply a migration (adr/0056). "
                + $"Callers: {string.Join(", ", offenders)}");
        }
    }

    /// <summary>
    /// The same rule one level up, and the reason <c>SchemaMigrationApplier</c> is a separate type from
    /// <c>SchemaVersionCheck</c> at all: "does this host reference the applier" is a question a reviewer
    /// can answer from the using directives, where "does it call MigrateAsync somewhere" is not.
    /// A host that wrapped the call in a helper would slip past the IL rule above and fail here.
    /// </summary>
    [Fact]
    public void ServingHosts_NeverReferenceTheApplier()
    {
        foreach (var host in TestAssemblies.ServingHosts)
        {
            Types.InAssembly(host.Reflection)
                .Should()
                .NotHaveDependencyOn(ApplierType)
                .GetResult()
                .ShouldPass($"{host.Name} must not reference SchemaMigrationApplier - applying a migration "
                    + "is Ago.Chat.Migrator's job and nothing else's (adr/0056)");
        }
    }

    /// <summary>
    /// The positive half: the capability exists somewhere. Without this, deleting the applier outright
    /// would make every rule above pass, which is the classic way a ban outlives the thing it was
    /// protecting.
    /// </summary>
    [Fact]
    public void TheMigrator_DoesApplyMigrations()
    {
        var callers = IlMemberScanner.FindCallers(
            TestAssemblies.InfrastructurePostgres.Cecil, MigrateExtensions, "MigrateAsync");

        Assert.True(callers.Count > 0,
            "Nothing applies migrations any more. SchemaMigrationApplier is what Ago.Chat.Migrator "
            + "depends on; if it moved, move this rule with it.");

        Types.InAssembly(TestAssemblies.Migrator.Reflection)
            .That().HaveNameEndingWith("Runner")
            .Should()
            .HaveDependencyOn(ApplierType)
            .GetResult()
            .ShouldPass("Ago.Chat.Migrator must be the host that applies migrations");
    }

    /// <summary>
    /// `adr/0056`: "It references <c>Ago.Chat.Infrastructure.Postgres</c> and nothing above it." Stated
    /// in the csproj as a comment, and here as a fact - checked against <c>Ago.Chat.Migrator</c>'s own
    /// resolved NuGet restore graph (<see cref="ReferenceBoundaryRule"/>), not its compiled assembly's
    /// metadata: the compiler elides a <c>ProjectReference</c> nothing in the code calls, so a stray,
    /// unused reference to <c>Ago.Chat.Module</c> would satisfy an assembly-metadata check while still
    /// dragging that project - and everything it wires (RabbitMQ, Redis, S3, Keycloak) - into the
    /// migrator's publish output. `17-12` found and closed that gap; see
    /// <see cref="ReferenceBoundaryRule"/>'s own remarks for the full argument and
    /// <see cref="TheRule_FlagsAForbiddenNamePresentOnlyInTheRestoreGraph"/> for the proof that this
    /// version, unlike the one it replaced, actually catches an unused reference.
    /// </summary>
    [Fact]
    public void TheMigrator_ReferencesPersistenceAndNothingAboveIt()
    {
        string[] forbidden =
            ["Ago.Chat.Module", "Ago.Chat.Api", "Ago.Chat.Worker", "Ago.Chat.Webhooks"];

        var migratorDirectory = Path.Combine(SourceTreeLocator.FindSrcDirectory(), "Ago.Chat.Migrator");
        var offenders = ReferenceBoundaryRule.ForbiddenNamesPresent(migratorDirectory, forbidden);

        Assert.True(offenders.Count == 0,
            $"Ago.Chat.Migrator's restore graph resolves {string.Join(", ", offenders)} - adr/0056 "
            + "confines it to Ago.Chat.Infrastructure.Postgres and below. Checked against "
            + "obj/project.assets.json (17-12), which is not fooled by an unused ProjectReference the "
            + "way a compiled-assembly check is.");
    }

    /// <summary>
    /// <b>The rule, proven able to fail</b> - the same permanent-fixture-style reasoning
    /// <see cref="ModuleKeyLiteralGuardTests.TheRule_FlagsAStringLiteralOfAKnownModuleKey"/> uses: a rule
    /// that has only ever been observed passing is not evidence. This is the in-process form of the
    /// proof; the real-world form is adding <c>&lt;ProjectReference Include="..\Ago.Chat.Module\..." /&gt;</c>
    /// to <c>Ago.Chat.Migrator.csproj</c> alone (no code using it) and re-running
    /// <see cref="TheMigrator_ReferencesPersistenceAndNothingAboveIt"/> - which is exactly what `17-12`'s
    /// report demonstrates, because a synthetic <c>project.assets.json</c> cannot by itself show that the
    /// real restore step actually writes a forbidden project's name into the real file.
    /// </summary>
    [Fact]
    public void TheRule_FlagsAForbiddenNamePresentOnlyInTheRestoreGraph()
    {
        var tempDirectory = Path.Combine(Path.GetTempPath(), $"ReferenceBoundaryRuleTests_{Guid.NewGuid():N}");
        var objDirectory = Path.Combine(tempDirectory, "obj");
        Directory.CreateDirectory(objDirectory);
        try
        {
            File.WriteAllText(Path.Combine(objDirectory, "project.assets.json"), """
                {
                  "libraries": {
                    "Ago.Chat.Infrastructure.Postgres/1.0.0": { "type": "project" },
                    "Ago.Chat.Module/1.0.0": { "type": "project" },
                    "Npgsql/10.0.3": { "type": "package" }
                  }
                }
                """);

            var offenders = ReferenceBoundaryRule.ForbiddenNamesPresent(
                tempDirectory, ["Ago.Chat.Module", "Ago.Chat.Api", "Ago.Chat.Worker", "Ago.Chat.Webhooks"]);

            Assert.Equal(["Ago.Chat.Module"], offenders);
        }
        finally
        {
            Directory.Delete(tempDirectory, recursive: true);
        }
    }

    /// <summary>
    /// Every serving host must actually run the guard. The rules above stop a host from *applying* a
    /// migration; this one stops the opposite failure - a host that neither applies nor checks, which
    /// is precisely the state the system was in on 2026-08-25.
    ///
    /// <para>A new host added later starts out failing this, which is the intended cost: a host that
    /// serves traffic against Postgres has to say what it does about the schema.</para>
    /// </summary>
    [Fact]
    public void EveryServingHost_RunsTheSchemaGuard()
    {
        foreach (var host in TestAssemblies.ServingHosts)
        {
            var callers = IlMemberScanner.FindCallers(
                host.Cecil,
                "Ago.Chat.Infrastructure.Postgres.Schema.SchemaGuardHostExtensions",
                "EnsureSchemaIsCurrentAsync");

            Assert.True(callers.Count > 0,
                $"{host.Name} never calls EnsureSchemaIsCurrentAsync - it would start and serve traffic "
                + "against a schema older than the migrations it was compiled with (8-08).");
        }
    }
}
