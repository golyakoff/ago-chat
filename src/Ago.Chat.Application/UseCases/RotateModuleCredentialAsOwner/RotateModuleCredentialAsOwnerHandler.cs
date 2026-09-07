using Ago.Chat.Application.Abstractions;
using Ago.Chat.Domain;
using Ago.Platform.Kernel;

namespace Ago.Chat.Application.UseCases.RotateModuleCredentialAsOwner;

/// <summary>
/// `23-83`/`adr/0151`: mints a fresh credential and installs it on both sides, on the platform
/// owner's own behalf. The module-first-then-persist ordering is unchanged from the deleted tenant
/// handler's own reasoning (recorded there, and restated in `23-83`'s own report rather than here,
/// since the argument itself did not change - only who may call it and where the provisioning secret
/// comes from did).
///
/// <para><b>No <see cref="IPermissionChecker"/> call, structurally, not by oversight</b> - the same
/// reasoning <see cref="EnableModuleForSiteAsOwner.EnableModuleForSiteAsOwnerHandler"/>'s own remarks
/// give: the fact that authorizes this call is the <c>RequirePlatformOwner</c> policy on the route
/// that resolves this handler, which lives nowhere <see cref="IPermissionChecker"/> could check.</para>
/// </summary>
public sealed class RotateModuleCredentialAsOwnerHandler(
    IEnabledModuleRepository modules, IModuleRegistrationGateway registrationGateway,
    IModuleCredentialGenerator credentialGenerator, IModuleProvisioningSecretProvider provisioningSecrets)
{
    public async Task<Result<ModuleCredentialRotatedByOwner>> HandleAsync(
        RotateModuleCredentialAsOwner command, CancellationToken cancellationToken)
    {
        ModuleKey moduleKey;
        try
        {
            moduleKey = new ModuleKey(command.ModuleKey);
        }
        catch (ArgumentException ex)
        {
            return ConversationErrors.ModuleInvalid(ex.Message);
        }

        // `adr/0150`'s own pattern, restated for this second owner caller: read from Ago.Chat.Api's
        // own configuration, never from the caller - see IModuleProvisioningSecretProvider's own
        // remarks for why a null here is a deployment state this handler refuses per call rather than
        // something the host fails to boot over.
        if (provisioningSecrets.TryGet() is not { } provisioningSecret)
        {
            return ConversationErrors.ModuleProvisioningNotConfigured(
                "This deployment has not configured a module-provisioning secret yet, so the platform "
                + "owner cannot rotate a module's credential from here.");
        }

        var existing = await modules.GetAsync(command.SiteId, moduleKey, cancellationToken);
        if (existing is null)
        {
            return ConversationErrors.ModuleNotEnabled();
        }

        var newCredential = new ModuleCredential(credentialGenerator.NewCredential());

        try
        {
            await registrationGateway.RotateAsync(
                new ModuleRegistrationTarget(moduleKey, command.SiteId, existing.EntryPoint), newCredential,
                provisioningSecret, cancellationToken);
        }
        catch (ModuleUnreachableException ex)
        {
            return ConversationErrors.ModuleRegistrationFailed(ex.Message);
        }

        var rotated = existing.WithCredential(newCredential);
        await modules.UpdateAsync(rotated, cancellationToken);
        return new ModuleCredentialRotatedByOwner(newCredential);
    }
}
