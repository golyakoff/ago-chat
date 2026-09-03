using Ago.Chat.Application.Abstractions;
using Ago.Chat.Domain;
using Ago.Platform.Kernel;

namespace Ago.Chat.Application.UseCases.VerifyModuleRegistration;

/// <summary>
/// `22-11`'s own fourth Done-when, made concrete: reads this side's own <see cref="EnabledModule"/> row
/// and the module's own <see cref="IModuleRegistrationGateway.GetStatusAsync"/> answer, and reports
/// whether they agree. This is a detector, not a repair: a disagreement is surfaced, never silently
/// fixed - the operator (or whatever calls this) decides what "repair" means for their situation
/// (re-run <c>EnableModuleForSite</c>/`RotateModuleCredential` if Chat's own row is the one missing or
/// stale; escalate if the module's own row is the one that should not exist).
///
/// <para><b>What this honestly cannot prove.</b> "Both sides have a row" is not "both sides agree on
/// the same secret" - <see cref="ModuleRegistrationRemoteStatus"/> deliberately carries no credential
/// (the module-side status endpoint never returns one, the same "accepted, never returned" hygiene
/// every credential field in this codebase gets), so a genuinely mismatched secret (the module was
/// re-provisioned by hand with a different value, say) reads as "agree" here even though a real call
/// would fail. Closing that gap needs a value both sides can compare without either revealing its own
/// secret (a fingerprint/hash of the credential, exchanged the same way) - not built here; named as the
/// honest limit of what "detectable" means in this first cut.</para>
/// </summary>
public sealed class VerifyModuleRegistrationHandler(IEnabledModuleRepository modules, IPermissionChecker permissions, IModuleRegistrationGateway registrationGateway)
{
    public async Task<Result<ModuleRegistrationReconciliationResult>> HandleAsync(
        VerifyModuleRegistration command, CancellationToken cancellationToken)
    {
        var allowed = await permissions.HasPermissionAsync(
            command.RequestedBy, command.SiteId, Permission.SiteConfigure, cancellationToken);
        if (!allowed)
        {
            return ConversationErrors.Forbidden("Operator does not have permission to configure this site's modules.");
        }

        ModuleKey moduleKey;
        Uri entryPoint;
        ModuleProvisioningSecret provisioningSecret;
        try
        {
            moduleKey = new ModuleKey(command.ModuleKey);
            entryPoint = new Uri(command.EntryPoint, UriKind.Absolute);
            provisioningSecret = new ModuleProvisioningSecret(command.ProvisioningSecret);
        }
        catch (Exception ex) when (ex is ArgumentException or UriFormatException)
        {
            return ConversationErrors.ModuleInvalid(ex.Message);
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
