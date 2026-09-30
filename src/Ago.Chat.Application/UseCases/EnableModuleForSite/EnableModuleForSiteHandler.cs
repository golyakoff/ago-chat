using Ago.Chat.Application.Abstractions;
using Ago.Chat.Domain;
using Ago.Platform.Kernel;

namespace Ago.Chat.Application.UseCases.EnableModuleForSite;

/// <summary>
/// `26-316`: the tenant admin's own self-service enable - see <see cref="EnableModuleForSite"/>'s own
/// remarks for why this path was removed by `23-83`/`adr/0151` and why it is safe to reintroduce now
/// that `adr/0150`/`adr/0154` put the provisioning secret and the entry point in configuration instead of
/// the request body.
///
/// <para><b>A separate command/handler from
/// <see cref="EnableModuleForSiteAsOwner.EnableModuleForSiteAsOwnerHandler"/>, not a nullable-<see cref="OperatorId"/>
/// branch on it</b> - the identical reasoning that handler's own remarks give for staying separate from
/// this one, now restated from this side: the two differ in exactly one thing, <b>who may call and how
/// that is proven</b>. This handler proves it with an explicit
/// <see cref="IPermissionChecker"/> check on <see cref="Permission.SiteConfigure"/> against the caller's
/// own site; the owner handler proves it with the <c>RequirePlatformOwner</c> policy on its route and
/// checks no permission at all. Folding both onto one handler with a nullable operator id would be the
/// "one flag flips off every check" shape this codebase avoids - a missing id would silently skip the
/// permission gate. So the two live apart, and the only visible difference on the row they both write is
/// <see cref="EnabledModule.GrantedByOwner"/>: <see langword="false"/> here, <see langword="true"/> there.</para>
///
/// <para><b>Idempotent on an already-active module.</b> A toggle can be double-submitted; if this site
/// already has an active (non-expired, non-revoked) row for this key, the existing id is returned without
/// re-registering or minting a second active row. Only a fresh enable runs the module-first registration,
/// the credential mint, and the permission seeding below. This is the one deliberate behavioural
/// difference from the owner handler, which re-registers unconditionally as its repair mechanism
/// (`23-102`); a tenant toggling their own module has no repair scenario to serve, so a repeat is a no-op.</para>
///
/// <para><b>Seeds the module's own permissions, exactly like the owner grant.</b> Without this, an admin
/// could enable the calendar and find that nobody - not even themselves - holds <c>calendar:configure</c>
/// to use it (`23-102`/`adr/0151`). The seed is idempotent and runs before <see cref="IEnabledModuleRepository.SaveAsync"/>
/// for the same reason the owner handler orders it first.</para>
/// </summary>
public sealed class EnableModuleForSiteHandler(
    IPermissionChecker permissions, IEnabledModuleRepository modules, IEnabledModuleReadStore moduleReadStore,
    IModuleRegistrationGateway registrationGateway, IModuleProvisioningSecretProvider provisioningSecrets,
    IModuleEntryPointProvider entryPoints, IModulePermissionsProvider modulePermissions, IModuleCredentialGenerator credentialGenerator,
    IRoleRepository roles, ISiteRepository sites, IClock clock, IIdGenerator idGenerator)
{
    public async Task<Result<EnabledModuleId>> HandleAsync(
        EnableModuleForSite command, CancellationToken cancellationToken)
    {
        var allowed = await permissions.HasPermissionAsync(
            command.RequestedBy, command.SiteId, Permission.SiteConfigure, cancellationToken);
        if (!allowed)
        {
            return ConversationErrors.Forbidden("Operator does not have permission to enable a module for this site.");
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

        // Resolved from this deployment's own configuration, never from the caller - `adr/0154`
        // (entry point) and `adr/0150` (provisioning secret). A null here names the missing key rather
        // than falling back to a guess, exactly as the owner handler does.
        if (entryPoints.TryGet(moduleKey) is not { } entryPoint)
        {
            return ConversationErrors.ModuleEntryPointNotConfigured(
                $"This deployment has not declared an entry point for module '{moduleKey.Value}' - set "
                + $"ModuleEntryPoints:{moduleKey.Value} before enabling it.");
        }

        if (provisioningSecrets.TryGet() is not { } provisioningSecret)
        {
            return ConversationErrors.ModuleProvisioningNotConfigured(
                "This deployment has not configured a module-provisioning secret yet, so a module cannot "
                + "be enabled from here.");
        }

        var now = clock.UtcNow;

        // See EnableModuleForSiteAsOwnerHandler's own identical guard for why the reserved-word check
        // runs ahead of the per-site overlap loop below.
        var reservedConflict = command.TriggerWords.FirstOrDefault(ReservedChatCommands.IsReserved);
        if (reservedConflict is not null)
        {
            return ConversationErrors.ModuleTriggerWordReserved(reservedConflict);
        }

        var existingOnSite = await moduleReadStore.GetForSiteAsync(command.SiteId, now, cancellationToken);
        foreach (var existing in existingOnSite)
        {
            // Idempotent: this module is already active for this site, so a repeat enable is a no-op
            // that returns the row already present rather than minting a second active one.
            if (existing.ModuleKey == moduleKey)
            {
                var current = await modules.GetAsync(command.SiteId, moduleKey, cancellationToken);
                return current is not null
                    ? current.Id
                    // The read store saw an active row the write store then could not find (a revoke
                    // landing between the two reads); fall through would double-register, so refuse
                    // rather than guess. Practically unreachable; refused legibly if it ever happens.
                    : ConversationErrors.ModuleNotEnabled();
            }

            var conflictingWord = existing.TriggerWords.FirstOrDefault(existingWord =>
                command.TriggerWords.Any(candidate => string.Equals(candidate, existingWord, StringComparison.OrdinalIgnoreCase)));
            if (conflictingWord is not null)
            {
                return ConversationErrors.ModuleTriggerWordAlreadyRegistered(conflictingWord, existing.ModuleKey.Value);
            }
        }

        ModuleCredential credential;
        EnabledModule module;
        try
        {
            credential = new ModuleCredential(credentialGenerator.NewCredential());
            module = new EnabledModule(
                new EnabledModuleId(idGenerator.NewId(now)), command.SiteId, moduleKey, command.TriggerWords,
                entryPoint, credential, now, grantedByOwner: false, expiresAt: null);
        }
        catch (ArgumentException ex)
        {
            return ConversationErrors.ModuleInvalid(ex.Message);
        }

        var site = await sites.GetByIdAsync(command.SiteId, cancellationToken);
        var displayName = string.IsNullOrWhiteSpace(site?.Name) ? $"site-{command.SiteId.Value}" : site.Name;

        // `22-11`'s own ordering, unchanged: the module confirms before this row is ever persisted.
        try
        {
            await registrationGateway.RegisterAsync(
                new ModuleRegistrationTarget(moduleKey, command.SiteId, entryPoint), credential, provisioningSecret,
                displayName, cancellationToken);
        }
        catch (ModuleUnreachableException ex)
        {
            return ConversationErrors.ModuleRegistrationFailed(ex.Message);
        }

        // `23-102`/`adr/0151`: seed the module's own vocabulary into the site's roles before persisting
        // the entitlement, so an enable never leaves a module nobody can act on. Idempotent.
        var modulePermissionSet = modulePermissions.Get(moduleKey);
        await roles.AddPermissionsAsync(command.SiteId, "Operator", modulePermissionSet.OperatorPermissions, cancellationToken);
        await roles.AddPermissionsAsync(command.SiteId, "Admin", modulePermissionSet.AdminPermissions, cancellationToken);

        await modules.SaveAsync(module, cancellationToken);

        return module.Id;
    }
}
