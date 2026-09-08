using Ago.Chat.Domain;

namespace Ago.Chat.Application.Abstractions;

/// <summary>
/// `23-102`/`adr/0151`: the platform's own act of enabling a module for a site now seeds that module's
/// permissions into the site's own "Operator"/"Admin" roles - the same act
/// <see cref="UseCases.RegisterSite.RegisterSiteHandler"/> performs at registration, performed at grant
/// time instead. This port is what supplies "which permissions does module K need", the identical shape
/// <see cref="IModuleEntryPointProvider"/> already gives "which address does module K live at".
///
/// <para><b>Keyed by whatever <see cref="ModuleKey"/> the caller supplies, never a fixed set of
/// properties - the identical reasoning <see cref="IModuleEntryPointProvider"/>'s own remarks give.</b>
/// Chat must not learn what a module is (`adr/0065` decision 2), so this cannot be an options class with
/// one property per known module, or a switch on <see cref="ModuleKey.Value"/>, without becoming the
/// exact literal <c>Ago.Chat.Architecture.Tests.ModuleKeyLiteralRule</c> exists to catch. A lookup by an
/// opaque key, resolved against whatever the deployment declares, keeps this assembly ignorant of which
/// keys exist while still letting the deployment supply the mapping - see
/// <see cref="Infrastructure.Modules.ConfiguredModulePermissionsProvider"/>'s own remarks for how that is
/// implemented without a bound POCO or a keyed branch.</para>
///
/// <para><b>An empty <see cref="ModulePermissionSet"/> is a legitimate answer, unlike
/// <see cref="IModuleEntryPointProvider"/>'s <see langword="null"/>.</b> A module genuinely needing no
/// permission beyond what every site already holds (nothing to configure, nothing to act on that
/// <c>conversation:*</c>/<c>site:*</c> does not already cover) is a real case, not a misconfiguration - so
/// an unset key resolves to <see cref="ModulePermissionSet.Empty"/> rather than refusing the grant the
/// way a missing entry point does. A module that <em>does</em> need permissions but whose deployment
/// forgot to declare them fails silently in the sense that the grant still succeeds and nobody can use
/// the module - the same shape this whole item exists to close, one level down. There is no mechanical
/// way to tell "genuinely none" from "forgotten" from inside this assembly (which is exactly why Chat
/// must not know what a module is), so this is deliberately left to whoever configures a deployment,
/// the same trust `ModuleEntryPoints` already places in that person for the address.</para>
///
/// <para><b>Restates, rather than reuses, `RegisterSiteHandler`'s own calendar permission literals.</b>
/// See that class's own remarks (the `22-05`/`adr/0093` block) for the historical list this
/// configuration mirrors, and <c>Infrastructure.Modules.ConfiguredModulePermissionsProvider</c>'s own
/// remarks for why a fourth restatement, not a shared reference, is what the architecture guard leaves
/// available.</para>
/// </summary>
public interface IModulePermissionsProvider
{
    /// <summary><see cref="ModulePermissionSet.Empty"/> when this deployment has declared no permissions
    /// for <paramref name="moduleKey"/> - a legitimate "this module needs nothing extra" answer, not a
    /// refusal.</summary>
    ModulePermissionSet Get(ModuleKey moduleKey);
}

/// <summary>The two permission lists a module grant may add to a site's seeded roles - split the same
/// way <see cref="UseCases.RegisterSite.RegisterSiteHandler"/> already splits its own baseline: day-to-day
/// action permissions join "Operator", configuration permissions join "Admin".</summary>
public sealed record ModulePermissionSet(IReadOnlyList<string> OperatorPermissions, IReadOnlyList<string> AdminPermissions)
{
    public static readonly ModulePermissionSet Empty = new([], []);
}
