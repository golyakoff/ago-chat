using System.Text.Json;

namespace Ago.Chat.Architecture.Tests;

/// <summary>
/// `17-12`: reads a project's own <c>obj/project.assets.json</c> - NuGet restore's resolved
/// dependency graph - instead of its compiled assembly's metadata.
///
/// <para><b>Why the compiled assembly is the wrong place to look.</b> <see cref="SchemaMigrationTests"/>'
/// original version of this check read <c>AssemblyDefinition.MainModule.AssemblyReferences</c> (Mono.Cecil,
/// same technique <see cref="IlMemberScanner"/> uses elsewhere) - but the C# compiler elides an assembly
/// reference nothing in the project's code actually uses. A <c>&lt;ProjectReference&gt;</c> to a forbidden
/// project, added to a host's <c>.csproj</c> and never referenced from a single line of code, compiles to
/// an assembly with no trace of it. Proven directly: the reference was added to
/// <c>Ago.Chat.Migrator.csproj</c> alone, the project rebuilt, and the Cecil-based check still passed -
/// removing the reference again changed nothing it could see. A guard that reads as stronger than it is,
/// is worse than no guard, because it stops anyone looking.</para>
///
/// <para><b>Why restore's graph, not the compiled one.</b> <c>dotnet restore</c> - which an ordinary build
/// runs implicitly - resolves every <c>&lt;ProjectReference&gt;</c> and <c>&lt;PackageReference&gt;</c>,
/// direct and transitive, and writes all of them to <c>project.assets.json</c>'s <c>libraries</c> section
/// before the compiler ever runs, let alone decides what to keep. Restore does not trim for use, so an
/// entry survives there whether or not any code touches it - which is exactly the property a
/// reference-boundary guard needs, since the boundary being enforced (`adr/0056`: "it references
/// <c>Ago.Chat.Infrastructure.Postgres</c> and nothing above it") is a statement about what the project
/// depends on, not about what its code happens to call.</para>
///
/// <para><b>Why <c>project.assets.json</c> over the generated <c>.deps.json</c>.</b> Both artifacts
/// carry the forbidden name once an unused reference is added (checked directly on
/// <c>Ago.Chat.Migrator</c>). <c>project.assets.json</c> won out on two practical grounds:
/// it lives at one fixed path - <c>&lt;project directory&gt;/obj/project.assets.json</c> - independent
/// of build <c>Configuration</c> and target framework, where <c>.deps.json</c>'s path
/// (<c>bin/&lt;Config&gt;/&lt;TFM&gt;/&lt;Name&gt;.deps.json</c>) embeds both; and it comes from restore
/// alone, so its existence does not depend on the project's <c>OutputType</c> generating a runtime
/// dependency file (an ordinary class library does not, by default - only an executable or web host
/// does), which matters because this rule exists to be reused by more than one one-shot host.</para>
///
/// <para><b>Why not parse the <c>.csproj</c> itself</b> (the more obvious-looking fix): a forbidden
/// reference injected by a shared <c>Directory.Build.props</c> rather than written directly in the
/// project file would not appear in the text of the <c>.csproj</c> at all, and would still slip past. A
/// project's effective reference list only exists after MSBuild evaluation - which is exactly what
/// restore performs before writing <c>project.assets.json</c>.</para>
/// </summary>
internal static class ReferenceBoundaryRule
{
    /// <summary>Every project and package name in <paramref name="projectDirectory"/>'s resolved
    /// restore graph - direct and transitive, used by the compiled code or not. Keys in
    /// <c>project.assets.json</c>'s <c>libraries</c> section look like <c>"Ago.Chat.Module/1.0.0"</c>
    /// (a project) or <c>"Npgsql/10.0.3"</c> (a package); only the name half is returned.</summary>
    public static IReadOnlySet<string> ResolvedLibraryNames(string projectDirectory)
    {
        var assetsPath = Path.Combine(projectDirectory, "obj", "project.assets.json");
        if (!File.Exists(assetsPath))
        {
            throw new InvalidOperationException(
                $"No {assetsPath}. The project has not been restored - `dotnet restore` or `dotnet build` "
                + "writes this file before any test can trust it.");
        }

        return ResolvedLibraryNamesFromAssetsFile(assetsPath);
    }

    /// <summary>The parsing core, separated from <see cref="ResolvedLibraryNames"/> so a fails-before
    /// proof can hand it a real, standalone <c>project.assets.json</c> on disk without needing a whole
    /// project directory tree behind it - the same "give the rule a real file, not a string held only in
    /// memory" reasoning <see cref="ModuleKeyLiteralRule.ScanFile"/> exists for.</summary>
    internal static IReadOnlySet<string> ResolvedLibraryNamesFromAssetsFile(string assetsFilePath)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(assetsFilePath));
        var libraries = document.RootElement.GetProperty("libraries");

        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var library in libraries.EnumerateObject())
        {
            var slash = library.Name.IndexOf('/');
            names.Add(slash < 0 ? library.Name : library.Name[..slash]);
        }

        return names;
    }

    /// <summary>The check itself: which of <paramref name="forbidden"/> names appear anywhere in
    /// <paramref name="projectDirectory"/>'s resolved restore graph.</summary>
    public static IReadOnlyList<string> ForbiddenNamesPresent(string projectDirectory, IReadOnlyCollection<string> forbidden)
    {
        var resolved = ResolvedLibraryNames(projectDirectory);
        return forbidden.Where(resolved.Contains).ToList();
    }
}
