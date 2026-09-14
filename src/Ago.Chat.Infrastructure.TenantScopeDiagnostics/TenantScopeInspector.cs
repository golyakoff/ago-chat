using Ago.Chat.Application.Abstractions;
using Mono.Cecil;

namespace Ago.Chat.Infrastructure.TenantScopeDiagnostics;

/// <summary>
/// `24-17`: <see cref="ITenantScopeInspector"/>'s real implementation - the adapter half of the port,
/// reading a concrete external resource (`Ago.Chat.Application.dll`, on disk, next to whichever host
/// process is running) with Mono.Cecil, the identical technology and the identical
/// <see cref="TenantScopeRule.Scan"/> the architecture test's build-time guard already uses.
///
/// <para><b>Why this can find the assembly by a bare file name.</b> Every serving host
/// (`Ago.Chat.Api` included) references `Ago.Chat.Application` as a project reference, so MSBuild
/// copies its compiled output next to the host's own - the identical guarantee
/// `Ago.Chat.Architecture.Tests/TestAssemblies.cs`'s own remarks state for why *that* project can load
/// nine assemblies "by simple name... instead of a fragile hard-coded output path". `AppContext.BaseDirectory`
/// is that host's own output directory at runtime, in a test host and in `Ago.Chat.Api` alike, which is
/// exactly what lets this same class be exercised directly from a test without any web host running.</para>
///
/// <para><b>Not cached.</b> The scan reads one file and walks its IL in well under the tool's own
/// 1.5-second, two-script budget (`tools/tenant-isolation-scan/README.md`) - fast enough that computing
/// it fresh on every call to an owner-only, low-traffic endpoint costs nothing worth guarding, and a
/// cached answer would be exactly the kind of "written down and can go stale" fact this whole item
/// exists to stop being one (CLAUDE.md rule 8 also rules out caching it: this is a fact a security
/// finding could depend on, so it must be re-read from the real assembly every time, never carried
/// forward from a previous read).</para>
/// </summary>
public sealed class TenantScopeInspector : ITenantScopeInspector
{
    private const string ApplicationAssemblyFileName = "Ago.Chat.Application.dll";

    private readonly string _applicationAssemblyPath;

    public TenantScopeInspector()
        : this(Path.Combine(AppContext.BaseDirectory, ApplicationAssemblyFileName))
    {
    }

    /// <summary>Internal seam for a test that wants to point this at a different assembly (e.g. this
    /// same test host's own `.dll`, the way <c>TenantScopeTests.TheRule_FlagsAHandlerThatTakesASiteIdAndNeverChecksPermission</c>
    /// points <see cref="TenantScopeRule.Scan"/> at itself) without touching the production
    /// constructor's zero-argument shape.</summary>
    internal TenantScopeInspector(string applicationAssemblyPath)
    {
        _applicationAssemblyPath = applicationAssemblyPath;
    }

    /// <summary>Genuinely synchronous - Mono.Cecil has no async API to sync-over, and reading one
    /// assembly's IL is not the kind of I/O CLAUDE.md rule 3 is written against (a blocking wait on
    /// another `Task`). <see cref="Task.FromResult{TResult}"/> is what lets the port's signature match
    /// every other <c>Application.Abstractions</c> port's async shape without pretending to await
    /// something that never yields.</summary>
    public Task<TenantScopeSnapshot> GetSnapshotAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(Scan(_applicationAssemblyPath));
    }

    /// <summary>The computation itself, exposed as a `static` method (rather than folded into
    /// <see cref="GetSnapshotAsync"/>) so a test can call it directly against an arbitrary assembly
    /// path - the same "prove the rule can fail" shape `TenantScopeTests` already uses for
    /// <see cref="TenantScopeRule.Scan"/> itself, applied one level up.</summary>
    public static TenantScopeSnapshot Scan(string assemblyPath)
    {
        using var assembly = AssemblyDefinition.ReadAssembly(assemblyPath);
        var entryPoints = TenantScopeRule.Scan(assembly);

        var exemptKeys = TenantScopeExemptions.ByEntryPoint.Keys;
        var gatedKeys = entryPoints.Where(e => e.IsRbacGated).Select(e => e.Key).ToHashSet(StringComparer.Ordinal);
        var exempt = entryPoints.Where(e => exemptKeys.Contains(e.Key)).ToList();

        var unaccounted = entryPoints
            .Where(e => !gatedKeys.Contains(e.Key) && !exemptKeys.Contains(e.Key))
            .Select(e => e.Key)
            .ToList();

        var exemptButAlsoLooksGated = exempt
            .Where(e => gatedKeys.Contains(e.Key))
            .Select(e => e.Key)
            .ToList();

        var handlerClasses = entryPoints
            .Select(e => e.Key[..e.Key.LastIndexOf('.')])
            .Distinct(StringComparer.Ordinal)
            .Count();

        return new TenantScopeSnapshot(
            EntryPoints: entryPoints.Count,
            HandlerClasses: handlerClasses,
            RbacGated: gatedKeys.Count,
            ExemptListed: exempt.Count,
            UnaccountedKeys: unaccounted,
            ExemptButAlsoLooksGated: exemptButAlsoLooksGated);
    }
}
