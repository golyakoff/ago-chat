using Ago.Chat.Application.Abstractions;
using Ago.Chat.Domain;
using Ago.Platform.Kernel;

namespace Ago.Chat.Application.UseCases.RevokeModuleForSiteAsOwner;

/// <summary>
/// `22-17`: the platform owner's own revoke - proves the grant is a real entitlement rather than a
/// one-way door. Identical module-first ordering to <c>RevokeModuleForSiteHandler</c> (revoke on the
/// module deployment before deleting Chat's own row, so a call in the gap cannot still succeed), and
/// the identical reason a separate class exists rather than a nullable-<see cref="OperatorId"/> branch
/// on that handler: see <see cref="EnableModuleForSiteAsOwner.EnableModuleForSiteAsOwnerHandler"/>'s
/// own remarks, which apply here unchanged. <see cref="IPermissionChecker"/> is never called -
/// <c>RequirePlatformOwner</c> on the route that resolves this handler is the entire access-control
/// story, the same single-gate shape every other owner surface in this codebase already uses.
///
/// <para><b>Works regardless of who granted the module.</b> This handler does not read
/// <see cref="EnabledModule.GrantedByOwner"/> - the platform owner may revoke a tenant's own
/// self-service purchase exactly as they may revoke their own grant, the same "the owner is not
/// scoped by what the tenant did" reasoning that makes <c>UnlinkChannelIdentityAsOwnerHandler</c>'s
/// own unlink unconditional. A narrower rule (owners can only revoke what owners granted) would leave
/// the support scenario this item's own brief opens with - a tenant's own broken registration -
/// without a remedy an owner can apply directly.</para>
/// </summary>
public sealed class RevokeModuleForSiteAsOwnerHandler(
    IEnabledModuleRepository modules, IModuleRegistrationGateway registrationGateway)
{
    public async Task<Result> HandleAsync(RevokeModuleForSiteAsOwner command, CancellationToken cancellationToken)
    {
        ModuleKey moduleKey;
        ModuleProvisioningSecret provisioningSecret;
        try
        {
            moduleKey = new ModuleKey(command.ModuleKey);
            provisioningSecret = new ModuleProvisioningSecret(command.ProvisioningSecret);
        }
        catch (ArgumentException ex)
        {
            return ConversationErrors.ModuleInvalid(ex.Message);
        }

        var existing = await modules.GetAsync(command.SiteId, moduleKey, cancellationToken);
        if (existing is null)
        {
            return ConversationErrors.ModuleNotEnabled();
        }

        try
        {
            await registrationGateway.RevokeAsync(
                new ModuleRegistrationTarget(moduleKey, command.SiteId, existing.EntryPoint), provisioningSecret,
                cancellationToken);
        }
        catch (ModuleUnreachableException ex)
        {
            return ConversationErrors.ModuleRegistrationFailed(ex.Message);
        }

        await modules.DeleteAsync(existing.Id, cancellationToken);
        return Result.Success();
    }
}
