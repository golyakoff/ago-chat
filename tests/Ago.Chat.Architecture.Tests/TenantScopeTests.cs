using Mono.Cecil;

namespace Ago.Chat.Architecture.Tests;

/// <summary>
/// `17-01`: `vision.md`'s loudest claim - "every piece of data is scoped by `site_id`" - held as a
/// rule that fails automatically instead of by review, the same shape `0-02` gave the layering rules.
///
/// <para>Every public entry point of every <c>*Handler</c> in <c>Ago.Chat.Application</c> must
/// either take a <c>SiteId</c> and gate it through <c>IPermissionChecker</c>, or be listed in
/// <see cref="TenantScopeExemptions"/> with a stated reason. Full classification, including which
/// route supplies each <c>SiteId</c> and what protects the exempt ones:
/// `ago-root/docs/architecture/tenant-isolation.md`.</para>
/// </summary>
public class TenantScopeTests
{
    [Fact]
    public void EveryUseCaseEntryPoint_IsEitherRbacGatedOrAnArguedExemption()
    {
        var unaccounted = TenantScopeRule.Scan(TestAssemblies.Application.Cecil)
            .Where(e => !e.IsRbacGated && !TenantScopeExemptions.ByEntryPoint.ContainsKey(e.Key))
            .Select(Describe)
            .ToList();

        Assert.True(
            unaccounted.Count == 0,
            "Every use case in Ago.Chat.Application must take a SiteId and check IPermissionChecker, or be listed "
            + "in TenantScopeExemptions with the reason it is safe without one (see "
            + "docs/architecture/tenant-isolation.md). Unaccounted for: " + string.Join("; ", unaccounted));
    }

    /// <summary>
    /// The other direction, and the one that keeps the list honest: an exemption whose entry point
    /// has since grown a real permission check, or has been renamed or deleted, is a stale claim
    /// sitting in a file whose entire value is that a reviewer can trust what it says.
    /// </summary>
    [Fact]
    public void NoExemption_IsStale()
    {
        var entryPoints = TenantScopeRule.Scan(TestAssemblies.Application.Cecil).ToDictionary(e => e.Key);

        var stale = TenantScopeExemptions.ByEntryPoint.Keys
            .Select(key => entryPoints.TryGetValue(key, out var entryPoint)
                ? entryPoint.IsRbacGated ? $"{key} (now RBAC-gated - drop the exemption)" : null
                : $"{key} (no such entry point - renamed or removed)")
            .OfType<string>()
            .ToList();

        Assert.True(stale.Count == 0, "Stale entries in TenantScopeExemptions: " + string.Join("; ", stale));
    }

    /// <summary>
    /// A permission check that is not scoped to a site answers a question nobody asked: `adr/0016`'s
    /// permissions are granted *per tenant*, so <c>HasPermissionAsync</c> without a caller-relevant
    /// <c>SiteId</c> in the same call could only be scoped to something invented inside the handler.
    /// No such handler exists today; this is what notices the first one.
    /// </summary>
    [Fact]
    public void EveryPermissionCheck_IsScopedToASiteTheEntryPointWasGiven()
    {
        var unscoped = TenantScopeRule.Scan(TestAssemblies.Application.Cecil)
            .Where(e => e.ChecksPermission && !e.CarriesSiteId)
            .Select(e => e.Key)
            .ToList();

        Assert.True(
            unscoped.Count == 0,
            "These use cases call IPermissionChecker but their command/query carries no SiteId, so the check is "
            + "scoped to a site the caller never named: " + string.Join("; ", unscoped));
    }

    /// <summary>
    /// <b>The rule, proven able to fail.</b> `0-02` demonstrated its layering rules by deliberately
    /// violating them; this does the same, permanently and in the build, rather than relying on
    /// whoever wrote the rule having checked it once by hand.
    ///
    /// <para><see cref="Fixtures.ForgetfulTenantScopedHandler"/> is exactly the mistake this item
    /// exists to catch - a use case that takes a <c>SiteId</c>, loads a row by an id the caller
    /// supplied, and never asks whether the caller may touch that site. Its compliant twin sits
    /// beside it so that a rule which flagged everything would fail here too.</para>
    /// </summary>
    [Fact]
    public void TheRule_FlagsAHandlerThatTakesASiteIdAndNeverChecksPermission()
    {
        var fixtures = TenantScopeRule.Scan(OwnAssembly())
            .Where(e => e.Key.StartsWith("Ago.Chat.Architecture.Tests.Fixtures.", StringComparison.Ordinal))
            .ToDictionary(e => e.Key);

        var forgetful = fixtures[
            "Ago.Chat.Architecture.Tests.Fixtures.ForgetfulTenantScopedHandler.HandleAsync"];
        var compliant = fixtures[
            "Ago.Chat.Architecture.Tests.Fixtures.CompliantTenantScopedHandler.HandleAsync"];

        Assert.True(forgetful.CarriesSiteId, "the violating fixture must genuinely be tenant-scoped input");
        Assert.False(forgetful.IsRbacGated, "the rule failed to flag a SiteId-carrying handler with no permission check");
        Assert.True(compliant.IsRbacGated, "the rule flagged a handler that does check IPermissionChecker");
    }

    private static AssemblyDefinition OwnAssembly() =>
        AssemblyDefinition.ReadAssembly(
            Path.Combine(AppContext.BaseDirectory, "Ago.Chat.Architecture.Tests.dll"));

    private static string Describe(TenantScopeRule.EntryPoint entryPoint) => entryPoint.CarriesSiteId
        ? $"{entryPoint.Key} (takes a SiteId, never calls IPermissionChecker)"
        : $"{entryPoint.Key} (no SiteId at all - so nothing scopes it to a tenant)";
}
