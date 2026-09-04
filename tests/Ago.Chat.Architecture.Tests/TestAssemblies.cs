using System.Reflection;
using Mono.Cecil;

namespace Ago.Chat.Architecture.Tests;

/// <summary>One assembly, two views: <see cref="Assembly"/> for NetArchTest's type-dependency
/// predicates, <see cref="AssemblyDefinition"/> (Mono.Cecil) for the IL-body checks NetArchTest has
/// no predicate for (a specific member call, an assembly's full reference list).</summary>
internal sealed record ProductAssembly(string Name, Assembly Reflection, AssemblyDefinition Cecil);

/// <summary>
/// Every assembly an arch test needs, loaded once. Domain/Application/Contracts have no public type
/// yet to anchor a <c>typeof(X).Assembly</c> lookup, so every assembly here is loaded uniformly by
/// its build output path instead - guaranteed to exist because this project's .csproj references
/// each one directly, which makes MSBuild copy it next to this test assembly's own output.
/// </summary>
internal static class TestAssemblies
{
    public static ProductAssembly Domain { get; } = Load("Ago.Chat.Domain");
    public static ProductAssembly Application { get; } = Load("Ago.Chat.Application");
    public static ProductAssembly Contracts { get; } = Load("Ago.Chat.Contracts");
    public static ProductAssembly InfrastructurePostgres { get; } = Load("Ago.Chat.Infrastructure.Postgres");
    public static ProductAssembly Module { get; } = Load("Ago.Chat.Module");
    public static ProductAssembly PlatformKernel { get; } = Load("Ago.Platform.Kernel");

    // `8-08`: the deployables. Loaded lazily like everything else here, by simple name from this
    // project's own output directory.
    public static ProductAssembly Api { get; } = Load("Ago.Chat.Api");
    public static ProductAssembly Worker { get; } = Load("Ago.Chat.Worker");
    public static ProductAssembly Webhooks { get; } = Load("Ago.Chat.Webhooks");
    public static ProductAssembly Migrator { get; } = Load("Ago.Chat.Migrator");
    public static ProductAssembly RoleAssignmentBackfill { get; } = Load("Ago.Chat.RoleAssignmentBackfill");

    /// <summary>The three hosts that serve traffic - `adr/0013`'s split. `8-08`'s rule is precisely
    /// that none of them may apply a schema migration; <see cref="Migrator"/> is deliberately not in
    /// this list, because it is the one that may.</summary>
    public static IReadOnlyList<ProductAssembly> ServingHosts { get; } = [Api, Worker, Webhooks];
    public static ProductAssembly PlatformHosting { get; } = Load("Ago.Platform.Hosting");

    /// <summary>Every product assembly the "time and identity only in Infrastructure" rule
    /// (adr/0011) applies to - i.e. everything except Infrastructure itself.</summary>
    public static IReadOnlyList<ProductAssembly> NonInfrastructure { get; } =
        [Domain, Application, Contracts, Module];

    /// <summary>Every product assembly, for rules with no layer exception (the CancellationToken rule).</summary>
    public static IReadOnlyList<ProductAssembly> AllProduct { get; } =
        [Domain, Application, Contracts, InfrastructurePostgres, Module];

    /// <summary>
    /// `14-06`: <b>every</b> <c>Ago.Chat.*</c> assembly, hosts included - the subject of
    /// <see cref="MessageOpacityTests"/>, whose rule has no layer exception at all and specifically
    /// must reach the hosts, since a branch on another product's vocabulary would most naturally
    /// appear in an endpoint or a hub rather than in Domain.
    ///
    /// <para>Wider than <see cref="AllProduct"/> on purpose, and kept as its own list rather than
    /// widening that one: <see cref="AllProduct"/>'s rules were written against five assemblies and
    /// silently extending them to nine is the sort of change that turns an unrelated test red for a
    /// reason nobody can attribute.</para>
    /// </summary>
    public static IReadOnlyList<ProductAssembly> EveryChatAssembly { get; } =
        [Domain, Application, Contracts, InfrastructurePostgres, Module, Api, Worker, Webhooks, Migrator, RoleAssignmentBackfill];

    private static ProductAssembly Load(string simpleName)
    {
        var path = Path.Combine(AppContext.BaseDirectory, $"{simpleName}.dll");
        var reflection = Assembly.LoadFrom(path);
        var cecil = AssemblyDefinition.ReadAssembly(path);
        return new ProductAssembly(simpleName, reflection, cecil);
    }
}
