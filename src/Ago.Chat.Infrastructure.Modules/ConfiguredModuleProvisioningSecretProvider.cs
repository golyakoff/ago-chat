using Ago.Chat.Application.Abstractions;
using Ago.Chat.Domain;

namespace Ago.Chat.Infrastructure.Modules;

/// <summary>
/// `23-65`/`adr/0150`: the real implementation of
/// <see cref="IModuleProvisioningSecretProvider"/> - reads <see cref="ModuleProvisioningOptions.Secret"/>,
/// bound once at startup from <c>ModuleProvisioning:Secret</c>, and turns it into a validated
/// <see cref="ModuleProvisioningSecret"/> on every call rather than once at construction: the plain
/// options value (not <c>IOptionsMonitor&lt;T&gt;</c>) is what every other options-backed secret in
/// this codebase is handed at DI registration time (<c>ChatModule.cs</c>'s own
/// <c>ChannelCredentialCipherOptions</c> precedent), so re-validating per call is the only way this
/// type can ever notice a value that was empty at boot and has since been set - which matters
/// precisely because this is the one options class in the codebase that is not required to be valid at
/// boot (<see cref="ModuleProvisioningOptions"/>'s own remarks).
/// </summary>
public sealed class ConfiguredModuleProvisioningSecretProvider(ModuleProvisioningOptions options)
    : IModuleProvisioningSecretProvider
{
    public ModuleProvisioningSecret? TryGet()
    {
        if (string.IsNullOrWhiteSpace(options.Secret))
        {
            return null;
        }

        try
        {
            return new ModuleProvisioningSecret(options.Secret);
        }
        catch (ArgumentException)
        {
            // A configured value that does not parse (too short, too long, all whitespace once
            // trimmed) gets the identical answer as an absent one - `IModuleProvisioningSecretProvider`'s
            // own remarks on why a caller never has to tell "unset" apart from "set wrong".
            return null;
        }
    }
}
