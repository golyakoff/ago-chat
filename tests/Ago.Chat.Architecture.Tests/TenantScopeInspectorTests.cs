using Ago.Chat.Infrastructure.TenantScopeDiagnostics;
using Xunit.Abstractions;

namespace Ago.Chat.Architecture.Tests;

/// <summary>
/// `24-17`: proves the runtime half (<see cref="TenantScopeInspector"/>, reached in production through
/// <c>Ago.Chat.Api</c>'s `GET /api/v1/owner/tenant-isolation`) reports the identical fact the
/// build-time half (<see cref="TenantScopeRule"/> + <see cref="TenantScopeExemptions"/>, walked
/// directly by <see cref="TenantScopeTests"/>) already enforces - not merely that both compile, but
/// that a real scan of the real, currently-built <c>Ago.Chat.Application.dll</c> produces the same five
/// numbers both ways. They cannot actually drift from each other (both call the same
/// <see cref="TenantScopeRule.Scan"/> against the same assembly), which is the point: this test is what
/// makes that "cannot drift" claim checked rather than assumed.
/// </summary>
public class TenantScopeInspectorTests(ITestOutputHelper output)
{
    [Fact]
    public void Snapshot_AgreesWithTheDirectScan_OnEveryHeadlineFigure()
    {
        var direct = TenantScopeRule.Scan(TestAssemblies.Application.Cecil);
        var exemptKeys = TenantScopeExemptions.ByEntryPoint.Keys;
        var directGated = direct.Where(e => e.IsRbacGated).ToList();
        var directExempt = direct.Where(e => exemptKeys.Contains(e.Key)).ToList();
        var directUnaccounted = direct
            .Where(e => !e.IsRbacGated && !exemptKeys.Contains(e.Key))
            .Select(e => e.Key)
            .ToList();

        var snapshot = TenantScopeInspector.Scan(
            Path.Combine(AppContext.BaseDirectory, "Ago.Chat.Application.dll"));

        // `24-17`'s own report needs the live numbers, not just a pass/fail - printed once here rather
        // than duplicated by hand into docs/architecture/tenant-isolation.md and left to go stale the
        // same way the item itself describes.
        output.WriteLine($"EntryPoints={snapshot.EntryPoints} HandlerClasses={snapshot.HandlerClasses} "
            + $"RbacGated={snapshot.RbacGated} ExemptListed={snapshot.ExemptListed} "
            + $"Unaccounted={snapshot.UnaccountedKeys.Count} "
            + $"ExemptButAlsoLooksGated={snapshot.ExemptButAlsoLooksGated.Count}");

        Assert.Equal(direct.Count, snapshot.EntryPoints);
        Assert.Equal(directGated.Count, snapshot.RbacGated);
        Assert.Equal(directExempt.Count, snapshot.ExemptListed);
        Assert.Equal(directUnaccounted.Count, snapshot.UnaccountedKeys.Count);
        Assert.Equal(directUnaccounted.OrderBy(k => k, StringComparer.Ordinal),
            snapshot.UnaccountedKeys.OrderBy(k => k, StringComparer.Ordinal));
    }

    /// <summary>The fails-before this item's own brief asks for, proven directly rather than only
    /// argued: point <see cref="TenantScopeInspector.Scan"/> at this test assembly's own compiled
    /// output, which carries <see cref="Fixtures.ForgetfulTenantScopedHandler"/> - a handler that takes
    /// a `SiteId` and never checks `IPermissionChecker`, and is not in <see cref="TenantScopeExemptions"/>
    /// because it names no real entry point. `Unaccounted` must include it; reverting to the real
    /// `Ago.Chat.Application.dll` (the test right above) is what shows it goes back to zero.</summary>
    [Fact]
    public void Snapshot_FlagsAnUngatedHandler_WhenScanningAnAssemblyThatHasOne()
    {
        var snapshot = TenantScopeInspector.Scan(
            Path.Combine(AppContext.BaseDirectory, "Ago.Chat.Architecture.Tests.dll"));

        Assert.Contains(
            "Ago.Chat.Architecture.Tests.Fixtures.ForgetfulTenantScopedHandler.HandleAsync",
            snapshot.UnaccountedKeys);
    }
}
