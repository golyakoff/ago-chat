using Ago.Chat.Domain;

namespace Ago.Chat.Application.Abstractions;

/// <summary>
/// `23-92`/`adr/0150`'s own reasoning, extended to a second field: the platform owner's own module
/// grant (<see cref="UseCases.EnableModuleForSiteAsOwner.EnableModuleForSiteAsOwnerHandler"/>) used to
/// take the module's entry point as raw caller input, the identical shape it took `adr/0095`'s
/// provisioning secret in before `adr/0150` moved that to configuration. The two fields are the same
/// kind of value - something the deployment already knows about itself, not something a person who can
/// read the cluster should have to retype - so this port applies the identical fix to the second field.
///
/// <para><b>Keyed by whatever <see cref="ModuleKey"/> the caller supplies, never a fixed set of
/// properties.</b> Unlike <see cref="IModuleProvisioningSecretProvider"/> (one deployment-wide value),
/// an entry point is per-module, and Chat must not learn what a module is
/// (<see cref="ModuleKey"/>'s own remarks, `adr/0065` decision 2) - so this port cannot be an options
/// class with one property named after each known module without becoming the exact literal the
/// architecture guard exists to catch. A lookup by an opaque key, resolved against whatever the
/// deployment declares, keeps this assembly ignorant of which keys exist while still letting the
/// deployment supply the mapping - see <see cref="Infrastructure.Modules.ConfiguredModuleEntryPointProvider"/>'s
/// own remarks for how that is implemented without a bound POCO.</para>
///
/// <para><b><see langword="null"/> is a legible refusal, never a hidden default.</b> A module the
/// deployment has not declared an entry point for must fail the grant with a message naming the
/// missing configuration key - not fall back to a blank, a guess, or the caller's own unchecked input,
/// which is precisely today's failure (a 404 from a plausible-looking address) with an extra step.</para>
/// </summary>
public interface IModuleEntryPointProvider
{
    /// <summary><see langword="null"/> when this deployment has declared no entry point for
    /// <paramref name="moduleKey"/>, or the declared value does not parse as an absolute http(s)
    /// URI.</summary>
    Uri? TryGet(ModuleKey moduleKey);
}
