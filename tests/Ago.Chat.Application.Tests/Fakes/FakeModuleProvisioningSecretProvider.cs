using Ago.Chat.Application.Abstractions;
using Ago.Chat.Domain;

namespace Ago.Chat.Application.Tests.Fakes;

/// <summary>`23-65`/`adr/0150`: a scripted double for
/// <see cref="IModuleProvisioningSecretProvider"/> - defaults to a valid, sixteen-plus-character
/// value so every existing owner-grant/revoke test keeps working unchanged, and lets a test null it
/// out to prove the "not configured yet" refusal (<c>ConversationErrors.ModuleProvisioningNotConfigured</c>).</summary>
public sealed class FakeModuleProvisioningSecretProvider : IModuleProvisioningSecretProvider
{
    public const string DefaultSecret = "a-provisioning-secret-of-sixteen-plus-chars";

    public ModuleProvisioningSecret? Secret { get; set; } = new ModuleProvisioningSecret(DefaultSecret);

    public ModuleProvisioningSecret? TryGet() => Secret;
}
