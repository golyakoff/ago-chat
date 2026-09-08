using Ago.Chat.Application.Abstractions;
using Ago.Chat.Domain;
using Microsoft.Extensions.Configuration;

namespace Ago.Chat.Infrastructure.Modules;

/// <summary>
/// `23-102`: the real implementation of <see cref="IModulePermissionsProvider"/> - reads the
/// <c>ModulePermissions</c> configuration section directly by whatever <see cref="ModuleKey"/> a caller
/// names, e.g. <c>ModulePermissions:calendar:Operator:0</c>, <c>:1</c>, ... (or, as environment variables,
/// <c>ModulePermissions__calendar__Operator__0</c> - the identical <c>__</c> convention
/// <c>ModuleEntryPoints__calendar</c> already uses). The exact mirror of
/// <see cref="ConfiguredModuleEntryPointProvider"/>, for the identical reason: see that class's own
/// remarks for why a bound options class or a switch on the key would become the literal
/// <c>ModuleKeyLiteralRule</c> exists to catch.
///
/// <para><b>Two arrays per module, not one.</b> <c>Operator</c> and <c>Admin</c> mirror the exact split
/// <see cref="UseCases.RegisterSite.RegisterSiteHandler"/>'s own two seeded permission arrays already
/// draw - day-to-day action permissions on the one role, configuration permissions on the other. A
/// missing child key (no <c>Operator</c> array, no <c>Admin</c> array, or the whole
/// <c>ModulePermissions:&lt;key&gt;</c> section itself) binds to an empty array, which is exactly
/// <see cref="ModulePermissionSet.Empty"/> - see that type's own remarks for why an unset key is a
/// legitimate answer here, unlike <see cref="IModuleEntryPointProvider"/>'s hard refusal.</para>
///
/// <para><b>A fourth restatement of the calendar permission list, not a reference to
/// <c>RegisterSiteHandler</c>'s own arrays.</b> That class's own remarks already name a restatement in
/// three independent places (itself, `1-05`'s seed script, `ago-deploy/seed/create-demo-tenant.sh`) for
/// the identical reason this is a fourth: <see cref="ModuleKey"/> must stay opaque to every assembly in
/// <c>Ago.Chat.*</c>, so nothing in this repository's compiled code can hold a
/// <c>"calendar" =&gt; [...]</c> branch that both call sites could share - only configuration data can be
/// keyed by an opaque string without becoming the literal the architecture guard exists to catch. The
/// deployment manifest that sets <c>ModuleEntryPoints__calendar</c> (`ago-deploy/k8s/base/api.yaml`) is
/// where the matching <c>ModulePermissions__calendar__Operator__*</c>/<c>Admin__*</c> entries belong,
/// mirroring the exact values <c>RegisterSiteHandler.OperatorRolePermissions</c>/
/// <c>AdminRolePermissions</c>' own calendar-specific entries carry today - see this item's own report
/// for why that deployment change is not part of this change.</para>
///
/// <para><b>Re-read on every call, not captured once</b> - the identical "a write decision never reads a
/// stale value" reasoning <see cref="ConfiguredModuleEntryPointProvider"/>'s own remarks give, restated
/// here for the same reason (`CLAUDE.md` rule 8).</para>
/// </summary>
public sealed class ConfiguredModulePermissionsProvider(IConfiguration configuration) : IModulePermissionsProvider
{
    /// <summary>The section a whole deployment's declared per-module permissions live under - one child
    /// section per module key, each carrying an <c>Operator</c> array and/or an <c>Admin</c> array of
    /// permission strings. Public for the identical reason <see cref="ConfiguredModuleEntryPointProvider.SectionName"/>
    /// is - a host's own configuration validation, or a test, references this name rather than a second
    /// copy of the string.</summary>
    public const string SectionName = "ModulePermissions";

    public ModulePermissionSet Get(ModuleKey moduleKey)
    {
        var moduleSection = configuration.GetSection(SectionName).GetSection(moduleKey.Value);
        var operatorPermissions = ReadArray(moduleSection, "Operator");
        var adminPermissions = ReadArray(moduleSection, "Admin");

        return operatorPermissions.Count == 0 && adminPermissions.Count == 0
            ? ModulePermissionSet.Empty
            : new ModulePermissionSet(operatorPermissions, adminPermissions);
    }

    private static IReadOnlyList<string> ReadArray(IConfigurationSection moduleSection, string roleName)
    {
        // `GetSection(...).Get<string[]>()` is .NET configuration's own array-binding convention
        // (numeric child keys `0`, `1`, ...) - the same shape every `:0`/`__0` array this codebase's
        // configuration already relies on elsewhere. Returns null (not an empty array) for a section
        // with no children at all, which the `?? []` below collapses into the same "nothing declared"
        // shape a genuinely empty declared array would have - this method's own caller does not need to
        // distinguish "never declared" from "declared as empty".
        var values = moduleSection.GetSection(roleName).Get<string[]>() ?? [];
        return values.Where(value => !string.IsNullOrWhiteSpace(value)).ToArray();
    }
}
