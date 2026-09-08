using Ago.Chat.Application.Abstractions;
using Ago.Chat.Domain;
using Microsoft.Extensions.Configuration;

namespace Ago.Chat.Infrastructure.Modules;

/// <summary>
/// `23-86`/`adr/0159`: the real implementation of <see cref="IBillingOptionEntitlementProvider"/> -
/// reads the <c>BillingOptionEntitlements</c> configuration section directly by whatever
/// <see cref="BillingOptionKey"/> a caller names, e.g. <c>BillingOptionEntitlements:channel-telegram</c>
/// (or, as an environment variable, <c>BillingOptionEntitlements__channel-telegram</c> - the identical
/// <c>__</c> convention <see cref="ConfiguredModuleEntryPointProvider"/> already uses), resolving to the
/// <see cref="ModuleKey"/> string that option turns on.
///
/// <para><b>Deliberately <see cref="IConfiguration"/> directly, not a bound options class</b> - the
/// identical reasoning <see cref="ConfiguredModuleEntryPointProvider"/>'s own remarks give: this
/// section's keys are billing option keys the deployment declares, opaque strings this assembly must
/// never enumerate or name, so there is no property list to bind onto that would not itself become the
/// literal the architecture guard exists to catch.</para>
///
/// <para><b>Re-read on every call, not captured once</b> - the identical "a write decision never reads
/// a stale value" reasoning <see cref="ConfiguredModuleEntryPointProvider"/>'s own remarks give for the
/// identical reason.</para>
/// </summary>
public sealed class ConfiguredBillingOptionEntitlementProvider(IConfiguration configuration) : IBillingOptionEntitlementProvider
{
    /// <summary>The section a whole deployment's option-to-entitlement mapping lives under - one child
    /// key per billing option, each the <see cref="ModuleKey"/> string that option grants. Public so a
    /// host's own configuration validation (or a test) can reference the identical name this class
    /// reads, rather than a second copy of the string.</summary>
    public const string SectionName = "BillingOptionEntitlements";

    public ModuleKey? TryGet(BillingOptionKey optionKey)
    {
        var raw = configuration.GetSection(SectionName)[optionKey.Value];
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        try
        {
            return new ModuleKey(raw);
        }
        catch (ArgumentException)
        {
            // A declared value that does not parse as a module key is treated exactly like an absent
            // one - the same "the caller has nothing more specific to do about either" collapsing
            // ConfiguredModuleEntryPointProvider's own remarks give for its sibling misconfiguration
            // case.
            return null;
        }
    }
}
