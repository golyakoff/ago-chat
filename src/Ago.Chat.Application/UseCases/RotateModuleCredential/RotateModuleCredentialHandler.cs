using Ago.Chat.Application.Abstractions;
using Ago.Chat.Domain;
using Ago.Platform.Kernel;

namespace Ago.Chat.Application.UseCases.RotateModuleCredential;

/// <summary>
/// `22-11`'s own second Done-when: "a credential can be rotated without downtime for other sites."
///
/// <para><b>Mints the new credential itself, unlike <c>EnableModuleForSiteHandler</c>.</b> The two
/// handlers were a real design choice, not an oversight: `EnableModuleForSite`'s contract is unchanged
/// from `22-02` (an operator-supplied credential, already shipped and tested - widening its blast
/// radius was judged not worth it for this item, see this repository's own report). Rotation has no
/// existing contract to preserve, and "give me a fresh, strong secret" is exactly what an operator asks
/// a rotate action for - minting with <see cref="IModuleCredentialGenerator"/> removes one place a
/// human could type something weak (this codebase's own <c>DemoCredentialGenerator</c>/
/// <c>WebhookSecretGenerator</c>/<c>OperatorInviteCodeGenerator</c> already establish "mint, don't
/// accept" as the house style for a freshly issued secret).</para>
///
/// <para><b>Module-first, then this row - the identical ordering
/// <c>EnableModuleForSiteHandler</c>'s own remarks argue for, and load-bearing for the same reason.</b>
/// If this handler updated <see cref="EnabledModule.Credential"/> before the module confirmed the
/// rotation, `Ago.Chat.*` would start minting every subsequent call with a credential the module does
/// not recognise yet - the exact downtime this item's own Done-when asks to avoid, worse than doing
/// nothing. Calling <see cref="IModuleRegistrationGateway.RotateAsync"/> first, and only updating this
/// row on its success, means this side never mints with a value the module has not already
/// accepted.</para>
/// </summary>
public sealed class RotateModuleCredentialHandler(
    IEnabledModuleRepository modules, IPermissionChecker permissions, IModuleRegistrationGateway registrationGateway,
    IModuleCredentialGenerator credentialGenerator)
{
    public async Task<Result<ModuleCredentialRotated>> HandleAsync(
        RotateModuleCredential command, CancellationToken cancellationToken)
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
        return new ModuleCredentialRotated(newCredential);
    }
}
