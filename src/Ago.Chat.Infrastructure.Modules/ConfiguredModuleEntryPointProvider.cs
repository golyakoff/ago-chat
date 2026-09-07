using Ago.Chat.Application.Abstractions;
using Ago.Chat.Domain;
using Microsoft.Extensions.Configuration;

namespace Ago.Chat.Infrastructure.Modules;

/// <summary>
/// `23-92`/`adr/0150`: the real implementation of <see cref="IModuleEntryPointProvider"/> - reads the
/// <c>ModuleEntryPoints</c> configuration section directly by whatever <see cref="ModuleKey"/> a caller
/// names, e.g. <c>ModuleEntryPoints:calendar</c> (or, as an environment variable,
/// <c>ModuleEntryPoints__calendar</c> - the identical <c>__</c> convention <c>ModuleProvisioning__Secret</c>
/// already uses).
///
/// <para><b>Deliberately <see cref="IConfiguration"/> directly, not a bound options class.</b> Every
/// other options-backed value in this codebase (<see cref="ModuleProvisioningOptions"/> included) binds
/// a section onto a class with one property per known key, because the set of keys is fixed at compile
/// time. This section's keys are module keys the deployment declares - opaque strings this assembly
/// must never enumerate or name (<see cref="IModuleEntryPointProvider"/>'s own remarks) - so there is
/// no property list to bind onto that would not itself become the literal the architecture guard exists
/// to catch. Reading the section generically by an indexer, at the moment a real caller names a real
/// key, is what keeps this class ignorant of which keys exist while still resolving whichever one is
/// asked for.</para>
///
/// <para><b>Re-read on every call, not captured once.</b> <see cref="IConfiguration"/> itself is the
/// live view (`.NET`'s own configuration providers apply changes to it as they are detected); this
/// class adds no caching of its own, the same "a write decision never reads a stale value" reasoning
/// `CLAUDE.md` rule 8 states for a database read, applied here to a configuration read that gates a
/// write.</para>
/// </summary>
public sealed class ConfiguredModuleEntryPointProvider(IConfiguration configuration) : IModuleEntryPointProvider
{
    /// <summary>The section a whole deployment's declared entry points live under - one child key per
    /// module, each an absolute http(s) URL. Public so a host's own configuration validation (or a
    /// test) can reference the identical name this class reads, rather than a second copy of the
    /// string.</summary>
    public const string SectionName = "ModuleEntryPoints";

    public Uri? TryGet(ModuleKey moduleKey)
    {
        var raw = configuration.GetSection(SectionName)[moduleKey.Value];
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        if (!Uri.TryCreate(raw, UriKind.Absolute, out var entryPoint)
            || (entryPoint.Scheme != Uri.UriSchemeHttp && entryPoint.Scheme != Uri.UriSchemeHttps))
        {
            // A declared value that does not parse is treated exactly like an absent one - the same
            // "the caller has nothing more specific to do about either" collapsing
            // ConfiguredModuleProvisioningSecretProvider's own remarks give for its sibling
            // misconfiguration cases.
            return null;
        }

        return entryPoint;
    }
}
