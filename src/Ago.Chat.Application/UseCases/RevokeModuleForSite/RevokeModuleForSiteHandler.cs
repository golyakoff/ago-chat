using Ago.Chat.Application.Abstractions;
using Ago.Chat.Domain;
using Ago.Platform.Kernel;

namespace Ago.Chat.Application.UseCases.RevokeModuleForSite;

/// <summary>
/// `22-11`'s own third Done-when: "revoking a site's registration refuses its subsequent calls."
/// Module-first here too, the identical ordering reasoning
/// <c>EnableModuleForSiteHandler</c>/<c>RotateModuleCredentialHandler</c> give: if this row were
/// deleted before the module confirmed the revoke, a call that reached the module in the gap between
/// the two would still succeed - the module is the side that actually answers a chat-originated call,
/// so it is the side whose state must change first for the Done-when's own claim ("refuses its
/// subsequent calls") to be true the instant this handler returns success.
/// </summary>
public sealed class RevokeModuleForSiteHandler(
    IEnabledModuleRepository modules, IPermissionChecker permissions, IModuleRegistrationGateway registrationGateway)
{
    public async Task<Result> HandleAsync(RevokeModuleForSite command, CancellationToken cancellationToken)
    {
        var allowed = await permissions.HasPermissionAsync(
            command.RequestedBy, command.SiteId, Permission.SiteConfigure, cancellationToken);
        if (!allowed)
        {
            return ConversationErrors.Forbidden("Operator does not have permission to configure this site's modules.");
        }

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
