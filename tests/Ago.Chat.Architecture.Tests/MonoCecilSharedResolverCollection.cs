namespace Ago.Chat.Architecture.Tests;

/// <summary>
/// `25-81`: <see cref="TenantScopeTests"/> and <see cref="TenantScopeInspectorTests"/> both call
/// <c>TenantScopeRule.Scan</c> against <c>TestAssemblies.Application.Cecil</c> - the one
/// <c>Mono.Cecil.AssemblyDefinition</c> <c>TestAssemblies</c> loads once, as a static property, for
/// the whole test process (see that class's own remarks on why). Reading an
/// <c>AssemblyDefinition</c> with no explicit <c>ReaderParameters</c> gives it one implicit
/// <c>DefaultAssemblyResolver</c>, and <c>Scan</c> calls <c>TypeReference.Resolve()</c> against it,
/// which caches what it resolves in an ordinary, non-thread-safe <c>Dictionary</c> - fine for one
/// caller, not for two threads writing to it at once.
///
/// <para><b>Why this is the whole fix.</b> xUnit parallelizes at the test-collection level, and every
/// class is its own implicit collection unless told otherwise - so, unfixed, these two classes'
/// <c>[Fact]</c>s could land on two threads at the same time and race on that one <c>Dictionary</c>,
/// which is exactly the <c>InvalidOperationException</c> `25-81` reproduced once, thrown from inside
/// <c>Mono.Cecil.DefaultAssemblyResolver.Resolve</c>. Putting both classes in this one named
/// collection is sufficient by itself: xUnit's own documented guarantee is that tests in the same
/// collection never run in parallel against each other, regardless of how many other collections run
/// alongside it. <c>DisableParallelization = true</c> is added on top, matching this item's own
/// suggested shape, so this collection additionally never overlaps any other collection's tests -
/// stronger than strictly required (no other class in this project calls <c>.Resolve()</c> against
/// <c>TestAssemblies.Application.Cecil</c>), cheap given how fast these IL scans are, and one line to
/// delete later if that stops being true.</para>
///
/// <para><b>Why no other class joins this collection.</b> <c>HubContractTests</c> also calls
/// <c>TypeReference.Resolve()</c>, but against <c>TestAssemblies.Api.Cecil</c> - a *different*
/// <c>AssemblyDefinition</c>, loaded by its own call to <c>AssemblyDefinition.ReadAssembly</c> in
/// <c>TestAssemblies.Load</c>, and therefore carrying its own separate implicit resolver and
/// dictionary. Two classes only race if they call <c>.Resolve()</c> against the *same*
/// <c>AssemblyDefinition</c>'s resolver, and no other class in this project resolves into
/// <c>Api.Cecil</c>, so <c>HubContractTests</c> has nothing to race with. Every other class that
/// touches <c>TestAssemblies.*.Cecil</c> (<c>LayeringTests</c>, <c>PersistenceBoundaryTests</c>,
/// <c>MessageOpacityTests</c>, <c>SchemaMigrationTests</c>, <c>TimeAndIdentityTests</c>) only reads
/// metadata already loaded into the module (<c>MainModule.AssemblyReferences</c>,
/// <c>MainModule.GetTypes()</c>, an instruction's <c>Operand</c> compared by name) - none of it calls
/// <c>.Resolve()</c>, so none of it touches the one non-thread-safe cache this collection exists to
/// serialize access to.</para>
///
/// <para><b>The alternative this item names</b> - giving <c>TestAssemblies.cs</c> an explicit,
/// synchronized resolver per loaded assembly - was rejected as more invasive for the hazard actually
/// found: it would touch the one piece of shared infrastructure all ~13 classes in this project
/// depend on, to fix a race only two of them can ever trigger. Collection scoping fixes exactly the
/// classes that race, leaves <see cref="TestAssemblies"/> exactly as it is, and stays legible as a
/// targeted fix rather than a defensive rewrite of code that was not the problem.</para>
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class MonoCecilSharedResolverCollection
{
    public const string Name = "Mono.Cecil shared resolver (TestAssemblies.Application.Cecil)";
}
