using Ago.Chat.Application.Abstractions;
using Ago.Chat.Domain;
using Ago.Platform.Kernel;

namespace Ago.Chat.Application.UseCases.VerifyModuleRegistrationAsOwner;

/// <summary>
/// `23-83`/`adr/0151`: reads this side's own <see cref="EnabledModule"/> row and the module's own
/// <see cref="IModuleRegistrationGateway.GetStatusAsync"/> answer, and reports whether they agree - on
/// the platform owner's own behalf. See the deleted tenant handler's own remarks (`23-83`'s report)
/// for what this honestly cannot prove; nothing about that changed, only who may call it.
///
/// <para><b>No <see cref="IPermissionChecker"/> call</b> - the identical structural reasoning
/// <see cref="RotateModuleCredentialAsOwner.RotateModuleCredentialAsOwnerHandler"/>'s own remarks
/// give for its own sibling.</para>
/// </summary>
public sealed class VerifyModuleRegistrationAsOwnerHandler(
    IEnabledModuleRepository modules, IModuleRegistrationGateway registrationGateway,
    IModuleProvisioningSecretProvider provisioningSecrets)
{
    public async Task<Result<ModuleRegistrationReconciliationResult>> HandleAsync(
        VerifyModuleRegistrationAsOwner command, CancellationToken cancellationToken)
    {
        ModuleKey moduleKey;
        Uri entryPoint;
        try
        {
            moduleKey = new ModuleKey(command.ModuleKey);
            entryPoint = new Uri(command.EntryPoint, UriKind.Absolute);
        }
        catch (Exception ex) when (ex is ArgumentException or UriFormatException)
        {
            return ConversationErrors.ModuleInvalid(ex.Message);
        }

        if (provisioningSecrets.TryGet() is not { } provisioningSecret)
        {
            return ConversationErrors.ModuleProvisioningNotConfigured(
                "This deployment has not configured a module-provisioning secret yet, so the platform "
                + "owner cannot verify a module's registration from here.");
        }

        var chatSideRow = await modules.GetAsync(command.SiteId, moduleKey, cancellationToken);
        var chatHasRegistration = chatSideRow is not null;

        ModuleRegistrationRemoteStatus remoteStatus;
        try
        {
            remoteStatus = await registrationGateway.GetStatusAsync(
                new ModuleRegistrationTarget(moduleKey, command.SiteId, entryPoint), provisioningSecret, cancellationToken);
        }
        catch (ModuleUnreachableException ex)
        {
            return ConversationErrors.ModuleRegistrationFailed(ex.Message);
        }

        return new ModuleRegistrationReconciliationResult(
            chatHasRegistration, remoteStatus.Exists, chatHasRegistration == remoteStatus.Exists);
    }
}
