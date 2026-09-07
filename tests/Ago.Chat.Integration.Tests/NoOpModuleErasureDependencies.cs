using Ago.Chat.Application.Abstractions;
using Ago.Chat.Domain;

namespace Ago.Chat.Integration.Tests;

/// <summary>
/// `22-30`: `SiteErasureJob` now resolves <see cref="IEnabledModuleReadStore"/> and
/// <see cref="IModuleRegistrationGateway"/> through an <see cref="IServiceScopeFactory"/> - see that
/// class's own `EraseModulesAsync` remarks. Every existing erasure test in this project (this file's
/// own siblings) seeds no <see cref="EnabledModule"/> row at all, so the module gate always finds an
/// empty list and returns immediately without ever calling the gateway - these two stand-ins exist only
/// so the job's constructor and DI graph are satisfiable, and deliberately fail loudly if a future test
/// that *does* seed a module forgets to replace them with something real.
/// </summary>
internal sealed class UncalledModuleRegistrationGateway : IModuleRegistrationGateway
{
    public Task RegisterAsync(
        ModuleRegistrationTarget module, ModuleCredential credential, ModuleProvisioningSecret provisioningSecret,
        string displayName, CancellationToken cancellationToken) =>
        throw new NotSupportedException("Not expected to be called - no EnabledModule row is seeded in this suite.");

    public Task RotateAsync(
        ModuleRegistrationTarget module, ModuleCredential newCredential, ModuleProvisioningSecret provisioningSecret,
        CancellationToken cancellationToken) =>
        throw new NotSupportedException("Not expected to be called - no EnabledModule row is seeded in this suite.");

    public Task RevokeAsync(
        ModuleRegistrationTarget module, ModuleProvisioningSecret provisioningSecret, CancellationToken cancellationToken) =>
        throw new NotSupportedException("Not expected to be called - no EnabledModule row is seeded in this suite.");

    public Task<ModuleRegistrationRemoteStatus> GetStatusAsync(
        ModuleRegistrationTarget module, ModuleProvisioningSecret provisioningSecret, CancellationToken cancellationToken) =>
        throw new NotSupportedException("Not expected to be called - no EnabledModule row is seeded in this suite.");

    public Task<TenantDataErasureResult> EraseTenantDataAsync(
        ModuleRegistrationTarget module, ModuleProvisioningSecret provisioningSecret, CancellationToken cancellationToken) =>
        throw new NotSupportedException("Not expected to be called - no EnabledModule row is seeded in this suite.");
}

/// <summary>Answers "not configured" - harmless here since the gateway above is never reached either
/// (this file's own remarks).</summary>
internal sealed class UnconfiguredModuleProvisioningSecretProvider : IModuleProvisioningSecretProvider
{
    public ModuleProvisioningSecret? TryGet() => null;
}
