using Ago.Chat.Application.Abstractions;
using Ago.Chat.Domain;

namespace Ago.Chat.Application.Tests.Fakes;

/// <summary>Records every call it receives and lets a test script a refusal - the identical shape
/// <see cref="FakeModuleGateway"/> already establishes for <see cref="IModuleGateway"/>'s own
/// sibling.</summary>
public sealed class FakeModuleRegistrationGateway : IModuleRegistrationGateway
{
    public List<(ModuleRegistrationTarget Module, ModuleCredential Credential, ModuleProvisioningSecret ProvisioningSecret, string DisplayName)> RegisterCalls { get; } = [];

    public List<(ModuleRegistrationTarget Module, ModuleCredential NewCredential, ModuleProvisioningSecret ProvisioningSecret)> RotateCalls { get; } = [];

    public List<(ModuleRegistrationTarget Module, ModuleProvisioningSecret ProvisioningSecret)> RevokeCalls { get; } = [];

    public List<(ModuleRegistrationTarget Module, ModuleProvisioningSecret ProvisioningSecret)> EraseTenantDataCalls { get; } = [];

    public bool UnreachableOnRegister { get; set; }

    public bool UnreachableOnRotate { get; set; }

    public bool UnreachableOnRevoke { get; set; }

    public bool UnreachableOnGetStatus { get; set; }

    public bool UnreachableOnEraseTenantData { get; set; }

    public ModuleRegistrationRemoteStatus StatusToReturn { get; set; } = new(Exists: true, DateTimeOffset.UtcNow, HasCredentialInGracePeriod: false);

    /// <summary>`22-30`: keyed by module key, so a test with more than one module on a site can script
    /// each one's own answer - defaults to "confirmed clean" for any module a test never scripts.</summary>
    public Dictionary<ModuleKey, TenantDataErasureResult> EraseTenantDataResultByModule { get; } = [];

    public Task RegisterAsync(
        ModuleRegistrationTarget module, ModuleCredential credential, ModuleProvisioningSecret provisioningSecret,
        string displayName, CancellationToken cancellationToken)
    {
        RegisterCalls.Add((module, credential, provisioningSecret, displayName));
        if (UnreachableOnRegister)
        {
            throw new ModuleUnreachableException(module.ModuleKey, "fake unreachable (register)");
        }

        return Task.CompletedTask;
    }

    public Task RotateAsync(
        ModuleRegistrationTarget module, ModuleCredential newCredential, ModuleProvisioningSecret provisioningSecret,
        CancellationToken cancellationToken)
    {
        RotateCalls.Add((module, newCredential, provisioningSecret));
        if (UnreachableOnRotate)
        {
            throw new ModuleUnreachableException(module.ModuleKey, "fake unreachable (rotate)");
        }

        return Task.CompletedTask;
    }

    public Task RevokeAsync(
        ModuleRegistrationTarget module, ModuleProvisioningSecret provisioningSecret, CancellationToken cancellationToken)
    {
        RevokeCalls.Add((module, provisioningSecret));
        if (UnreachableOnRevoke)
        {
            throw new ModuleUnreachableException(module.ModuleKey, "fake unreachable (revoke)");
        }

        return Task.CompletedTask;
    }

    public Task<ModuleRegistrationRemoteStatus> GetStatusAsync(
        ModuleRegistrationTarget module, ModuleProvisioningSecret provisioningSecret, CancellationToken cancellationToken)
    {
        if (UnreachableOnGetStatus)
        {
            throw new ModuleUnreachableException(module.ModuleKey, "fake unreachable (status)");
        }

        return Task.FromResult(StatusToReturn);
    }

    public Task<TenantDataErasureResult> EraseTenantDataAsync(
        ModuleRegistrationTarget module, ModuleProvisioningSecret provisioningSecret, CancellationToken cancellationToken)
    {
        EraseTenantDataCalls.Add((module, provisioningSecret));
        if (UnreachableOnEraseTenantData)
        {
            throw new ModuleUnreachableException(module.ModuleKey, "fake unreachable (erase tenant data)");
        }

        return Task.FromResult(
            EraseTenantDataResultByModule.GetValueOrDefault(
                module.ModuleKey, new TenantDataErasureResult(TenantExisted: true, Confirmed: true)));
    }
}
