using Ago.Chat.Application.Abstractions;
using Ago.Chat.Domain;
using Ago.Platform.Kernel;

namespace Ago.Chat.Application.UseCases.DisableModuleForSite;

/// <summary>
/// `26-316`: the tenant admin's own self-service disable - see <see cref="DisableModuleForSite"/>'s own
/// remarks. Identical module-first ordering to
/// <see cref="RevokeModuleForSiteAsOwner.RevokeModuleForSiteAsOwnerHandler"/>: the module deployment's own
/// revoke runs before Chat's row is stamped, so a call arriving in the gap cannot still authenticate.
///
/// <para><b>Non-destructive, by construction.</b> Disabling stamps <see cref="EnabledModule.RevokedAt"/>
/// (<see cref="EnabledModule.Revoke"/>) rather than deleting the row (`22-30`), and the module-side
/// <see cref="IModuleRegistrationGateway.RevokeAsync"/> deactivates the site's registration rather than
/// erasing the calendars and bookings behind it (that is what
/// <see cref="IModuleRegistrationGateway.EraseTenantDataAsync"/>, a different call this handler never
/// makes, is for). So a later re-enable through <see cref="EnableModuleForSite.EnableModuleForSiteHandler"/>
/// mints a fresh registration and the tenant's calendar data - keyed on the module's side by site, not by
/// registration - is reachable again. The two-rows-for-one-key shape a revoke-then-re-enable leaves on
/// Chat's side is `adr/0155`'s, unchanged.</para>
///
/// <para><b>Refuses to disable a platform-owner grant.</b> `26-316`'s decision keeps the owner grant as an
/// override; a tenant toggling their own settings must not be able to silently undo one. When
/// <see cref="EnabledModule.GrantedByOwner"/> is set, this returns
/// <see cref="ConversationErrors.ModuleDisableOwnerGrantRefused"/> (a 409) rather than revoking - the exact
/// mirror of <see cref="RevokeModuleForSiteAsOwner.RevokeModuleForSiteAsOwnerHandler"/>'s own refusal to
/// revoke a tenant purchase without force. The console reads
/// <see cref="EnabledModuleSummary.GrantedByOwner"/> and never offers the off control in that state, so
/// this is a defence in depth, not the tenant's normal experience.</para>
/// </summary>
public sealed class DisableModuleForSiteHandler(
    IPermissionChecker permissions, IEnabledModuleRepository modules, IModuleRegistrationGateway registrationGateway,
    IModuleProvisioningSecretProvider provisioningSecrets, IClock clock)
{
    public async Task<Result> HandleAsync(DisableModuleForSite command, CancellationToken cancellationToken)
    {
        var allowed = await permissions.HasPermissionAsync(
            command.RequestedBy, command.SiteId, Permission.SiteConfigure, cancellationToken);
        if (!allowed)
        {
            return ConversationErrors.Forbidden("Operator does not have permission to disable a module for this site.");
        }

        ModuleKey moduleKey;
        try
        {
            moduleKey = new ModuleKey(command.ModuleKey);
        }
        catch (ArgumentException ex)
        {
            return ConversationErrors.ModuleInvalid(ex.Message);
        }

        if (provisioningSecrets.TryGet() is not { } provisioningSecret)
        {
            return ConversationErrors.ModuleProvisioningNotConfigured(
                "This deployment has not configured a module-provisioning secret yet, so a module cannot "
                + "be disabled from here.");
        }

        var existing = await modules.GetAsync(command.SiteId, moduleKey, cancellationToken);
        if (existing is null)
        {
            return ConversationErrors.ModuleNotEnabled();
        }

        if (existing.GrantedByOwner)
        {
            return ConversationErrors.ModuleDisableOwnerGrantRefused(
                $"Module '{moduleKey.Value}' on this site was enabled by AGO, not from your own settings, so "
                + "it cannot be turned off here. Contact AGO to change it.");
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

        await modules.UpdateAsync(existing.Revoke(clock.UtcNow), cancellationToken);

        return Result.Success();
    }
}
