namespace Ago.Chat.Application.Abstractions;

/// <summary>
/// `24-17`: the runtime half of `17-01`'s tenant-scope rule - the same fact
/// <c>Ago.Chat.Architecture.Tests.TenantScopeTests</c> enforces at build time
/// (`Ago.Chat.Infrastructure.TenantScopeDiagnostics.TenantScopeRule.Scan`), asked of the assembly that
/// is actually running rather than of a checkout on a machine that may not have one.
///
/// <para><b>Why this is a port at all, rather than a static helper `Ago.Chat.Api` calls
/// directly.</b> The fact it answers is read by reflecting over a `.dll` on disk with Mono.Cecil - a
/// concrete external resource in exactly the sense CLAUDE.md rule 2 means: a file the process happens
/// to have, not a business rule <c>Ago.Chat.Application</c> could compute from its own in-memory
/// state. The alternative - giving <c>Ago.Chat.Application</c> a `PackageReference` on Mono.Cecil and
/// doing the `AssemblyDefinition.ReadAssembly` call inline - would put an IL-reading, filesystem-touching
/// library inside the layer the dependency rule keeps free of exactly that, and would make every other
/// consumer of <c>Application</c> (every future host, every test double) carry Mono.Cecil along for a
/// diagnostic almost none of them will ever call. A port lets `Ago.Chat.Api`'s owner-only endpoint ask
/// the question through the same seam every other external fact already comes through, and lets the
/// real answer live in one <c>Infrastructure.*</c> project shared with the architecture test - see that
/// project's own remarks for why the test references it too, instead of keeping its own copy.</para>
/// </summary>
public interface ITenantScopeInspector
{
    /// <summary>Scans <c>Ago.Chat.Application</c>'s own currently-loaded assembly and reports the same
    /// five figures `TenantScopeRule.Scan` + `TenantScopeExemptions` produce for the architecture
    /// test - a fact about the deployment that is actually running, not about a source checkout
    /// somebody may not have open.</summary>
    Task<TenantScopeSnapshot> GetSnapshotAsync(CancellationToken cancellationToken);
}
