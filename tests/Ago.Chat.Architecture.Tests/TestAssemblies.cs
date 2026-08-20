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
    public static ProductAssembly PlatformHosting { get; } = Load("Ago.Platform.Hosting");

    /// <summary>Every product assembly the "time and identity only in Infrastructure" rule
    /// (adr/0011) applies to - i.e. everything except Infrastructure itself.</summary>
    public static IReadOnlyList<ProductAssembly> NonInfrastructure { get; } =
        [Domain, Application, Contracts, Module];

    /// <summary>Every product assembly, for rules with no layer exception (the CancellationToken rule).</summary>
    public static IReadOnlyList<ProductAssembly> AllProduct { get; } =
        [Domain, Application, Contracts, InfrastructurePostgres, Module];

    private static ProductAssembly Load(string simpleName)
    {
        var path = Path.Combine(AppContext.BaseDirectory, $"{simpleName}.dll");
        var reflection = Assembly.LoadFrom(path);
        var cecil = AssemblyDefinition.ReadAssembly(path);
        return new ProductAssembly(simpleName, reflection, cecil);
    }
}
