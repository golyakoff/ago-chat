using Ago.Chat.Domain;

namespace Ago.Chat.Application.Abstractions;

/// <summary>
/// `23-65`/`adr/0150`: the platform owner's own module grant/revoke
/// (<see cref="UseCases.EnableModuleForSiteAsOwner.EnableModuleForSiteAsOwnerHandler"/>,
/// <see cref="UseCases.RevokeModuleForSiteAsOwner.RevokeModuleForSiteAsOwnerHandler"/>) used to take
/// `adr/0095`'s deployment-wide <see cref="ModuleProvisioningSecret"/> as raw caller input, the same
/// way the tenant's own self-service module endpoints still do
/// (<see cref="UseCases.EnableModuleForSite.EnableModuleForSite"/>'s own remarks - that route is
/// unchanged by this item). A console screen cannot put that secret in a request body without putting
/// it in a browser, so `adr/0150` moves the owner's two callers to a port instead:
/// <c>Ago.Chat.Api</c>'s own configuration supplies the value, and the platform owner's authorisation
/// (`RequirePlatformOwner`, unchanged) is what the call is trusted on.
///
/// <para><b>The port, not <c>IConfiguration</c> directly, is what Application depends on</b> -
/// `CLAUDE.md` rule 2's "every external resource sits behind a port" applied to configuration the same
/// way <see cref="IChannelCredentialCipher"/> already applies it to
/// `Channels:CredentialEncryptionKey`: the Application layer never binds a config section itself, it
/// asks a port for the value it needs. <c>Ago.Chat.Infrastructure.Modules</c>'s own implementation
/// reads <c>ModuleProvisioning:Secret</c>, bound once by the host the same way every other
/// options-backed secret in this codebase is.</para>
///
/// <para><b>Never on start, deliberately.</b> Every other config-bound secret in this codebase that
/// gates a live channel is validated with <c>.ValidateOnStart()</c>, so a misconfigured deployment
/// fails at boot rather than on first use. This one is not: `secrets.md` records that nothing on the
/// live deployment holds this value yet (`enabled_modules` has zero rows, and no module product is
/// deployed there today), so requiring it at every `Ago.Chat.Api` boot would crash-loop the entire
/// host - every conversation, every widget, every unrelated route - over one narrow admin feature that
/// nobody has finished provisioning. <see cref="TryGet"/> returning <see langword="null"/> is a normal,
/// expected deployment state (`adr/0095`'s own "an absent or empty configured secret never
/// authenticates anything... turning this on cannot happen by omission", extended here from the
/// module's own verification side to Chat's own sending side), refused per call with a `503` rather
/// than taken down at the process level.</para>
/// </summary>
public interface IModuleProvisioningSecretProvider
{
    /// <summary><see langword="null"/> when <c>ModuleProvisioning:Secret</c> is unset, blank, or does
    /// not parse as a valid <see cref="ModuleProvisioningSecret"/> - three different misconfigurations
    /// collapsed into one caller-facing answer, the same "the caller has nothing more specific to do
    /// about any of them" reasoning <see cref="Ago.Chat.Application.Abstractions.IModuleRegistrationGateway"/>'s
    /// own remarks give for collapsing its own failure causes into one exception type.</summary>
    ModuleProvisioningSecret? TryGet();
}
