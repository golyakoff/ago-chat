using Ago.Chat.Application.Abstractions;
using Ago.Chat.Domain;
using Ago.Platform.Kernel;

namespace Ago.Chat.Application.UseCases.EnableModuleForSiteAsOwner;

/// <summary>
/// `22-17`: the platform owner's own write surface for granting a product to a tenant with no
/// payment - sales trials and support repair, the two scenarios this item's own brief names
/// ("payment succeeded, provisioning did not" chief among them). Reuses `22-11`'s own
/// module-first-then-persist provisioning path (<see cref="IModuleRegistrationGateway.RegisterAsync"/>)
/// unchanged - this handler is the second caller of that exact mechanism, not a second way to make a
/// module registration exist. Nothing about "does the module confirm before Chat's own row is
/// written" differs between a tenant enabling their own module and an owner granting one; only *who
/// may call this* and *what the row records about how it got here* differ, which is exactly what a
/// separate command/handler exists to carry.
///
/// <para><b>A wholly separate command and handler from <see cref="EnableModuleForSite.EnableModuleForSiteHandler"/>,
/// not a nullable-<see cref="OperatorId"/> parameter on it.</b> The identical reasoning
/// <c>UnlinkChannelIdentityAsOwnerHandler</c>'s own remarks give for the platform owner's first write
/// surface, restated for this one: the fact that authorizes this call - the <c>RequirePlatformOwner</c>
/// policy on the route that resolves this handler - does not live in a table
/// <see cref="IPermissionChecker"/> could check, so a permission check here would be a second, weaker
/// copy of a decision the policy already made. A nullable-<see cref="OperatorId"/> branch on the
/// self-service handler instead would mean one caller's missing id silently skips
/// <see cref="Permission.SiteConfigure"/> on a handler whose only other caller requires it - the exact
/// "one flag flips off every check" shape this codebase avoids elsewhere by keeping owner surfaces in
/// their own class.</para>
///
/// <para><b>The audit distinction lives on the row itself.</b> The only behavioural difference from
/// <see cref="EnableModuleForSite.EnableModuleForSiteHandler"/>'s own write is
/// <see cref="EnabledModule.GrantedByOwner"/>: <see langword="true"/> here, always
/// <see langword="false"/> there. Everything else - the trigger-conflict check, the reserved-word
/// check, the module-first registration call - runs identically, because a grant a tenant cannot
/// distinguish from their own purchase in ordinary use (same entry point, same trigger words, same
/// working module) is the whole point: the distinction has to be *recorded*, not *felt*, or every
/// grant becomes a support conversation about why the tenant's own module "looks different".</para>
///
/// <para><b>Why a grant does not become the normal path (this item's own second brief question).</b>
/// Structurally, not by policy this handler enforces: the platform owner is a Keycloak realm role
/// (`adr/0032`) granted by hand, in Keycloak's own admin console, to nobody by default - it is not a
/// role a tenant, an operator invite, or any write in this codebase can ever confer on itself. A
/// tenant cannot reach this handler by any action available to them; only the one or few identities a
/// human administrator has separately, manually decided are the platform's own operators can. That is
/// a stronger guarantee than "harder to use" - a tenant motivated to avoid paying has no path to this
/// endpoint at all, the same reason `RequirePlatformOwner`'s own gate is sufficient authorization for
/// <see cref="ListSitesForOwner.ListSitesForOwnerHandler"/> and <c>UnlinkChannelIdentityAsOwnerHandler</c>
/// with no second check.</para>
///
/// <para><b>`23-102`/`adr/0151`: this call now also seeds the module's own permissions into the site's
/// seeded roles</b> - the entitlement (<see cref="EnabledModule"/>) and the vocabulary to act on it
/// (`Operator`/`Admin`'s own permission lists) land in the same call, so a grant a tenant's own
/// administrator opens never finds nobody able to use it. See <see cref="Abstractions.IModulePermissionsProvider"/>'s
/// own remarks for why this cannot be a literal `"calendar" =&gt; [...]` branch here, and this handler's
/// own body for why it runs before <see cref="Abstractions.IEnabledModuleRepository.SaveAsync"/>, not
/// after. Deliberately unconditional on whether the module was already enabled for this site: a re-grant
/// of an already-granted module re-runs the identical, idempotent seeding, which is how a site granted
/// before this item existed gets repaired - not a separate backfill.</para>
///
/// <para><b>`23-59`/`adr/0147`: this call now also requests a retroactive contact carry-over</b> -
/// see this handler's own remarks at the bottom of <see cref="HandleAsync"/> for why that request is
/// unconditional on which module was granted, and <c>IContactCarryoverRequestStore</c>'s own remarks
/// for why the actual work happens later, in <c>Ago.Chat.Worker.ContactCarryoverJob</c>, rather than
/// here.</para>
/// </summary>
public sealed class EnableModuleForSiteAsOwnerHandler(
    IEnabledModuleRepository modules, IEnabledModuleReadStore moduleReadStore,
    IModuleRegistrationGateway registrationGateway, IModuleProvisioningSecretProvider provisioningSecrets,
    IModuleEntryPointProvider entryPoints, IModulePermissionsProvider modulePermissions, IRoleRepository roles,
    IContactCarryoverRequestStore contactCarryover, ISiteRepository sites, IClock clock, IIdGenerator idGenerator)
{
    /// <summary>`22-17`'s own answer to "decide whether a grant carries an end date": it may, but an
    /// owner is not asked to type an unbounded one. A grant that never ends is legitimate (the repair
    /// scenario - restoring what a successful payment should have provisioned), so <see langword="null"/>
    /// is accepted; a grant that *does* carry a date is capped here at one year, an implementer's-call
    /// safety rail against a fat-fingered date far in the future being indistinguishable from "forever"
    /// - not a measured trial length and not a business rule (`CLAUDE.md`: "do not invent numbers... a
    /// typical production figure"). A real trial in this codebase's own vocabulary is weeks, not
    /// months; this bound exists only to catch a mistake, not to express a policy about how long a
    /// trial should run.</summary>
    public static readonly TimeSpan MaxGrantDuration = TimeSpan.FromDays(365);

    public async Task<Result<EnabledModuleId>> HandleAsync(
        EnableModuleForSiteAsOwner command, CancellationToken cancellationToken)
    {
        var now = clock.UtcNow;

        if (command.ExpiresAt is { } expiresAt)
        {
            if (expiresAt <= now)
            {
                return ConversationErrors.ModuleGrantExpiryInvalid("A module grant's expiry must be in the future.");
            }

            if (expiresAt > now + MaxGrantDuration)
            {
                return ConversationErrors.ModuleGrantExpiryInvalid(
                    $"A module grant cannot expire more than {MaxGrantDuration.TotalDays:0} days out - " +
                    "omit ExpiresAt for a grant with no end date instead.");
            }
        }

        ModuleKey moduleKey;
        ModuleCredential credential;
        try
        {
            moduleKey = new ModuleKey(command.ModuleKey);
            credential = new ModuleCredential(command.Credential);
        }
        catch (ArgumentException ex)
        {
            return ConversationErrors.ModuleInvalid(ex.Message);
        }

        // `23-92`/`adr/0154`: resolved from this deployment's own configuration, never from the
        // caller - see IModuleEntryPointProvider's own remarks for why a null here names the missing
        // key rather than falling back to a blank or a guess.
        if (entryPoints.TryGet(moduleKey) is not { } entryPoint)
        {
            return ConversationErrors.ModuleEntryPointNotConfigured(
                $"This deployment has not declared an entry point for module '{moduleKey.Value}' - set "
                + $"ModuleEntryPoints:{moduleKey.Value} before granting it.");
        }

        // `adr/0150`: read from Ago.Chat.Api's own configuration, never from the caller - see
        // IModuleProvisioningSecretProvider's own remarks for why a null here is a deployment state
        // this handler refuses per call rather than something the host fails to boot over.
        if (provisioningSecrets.TryGet() is not { } provisioningSecret)
        {
            return ConversationErrors.ModuleProvisioningNotConfigured(
                "This deployment has not configured a module-provisioning secret yet, so the platform "
                + "owner cannot grant a module from here.");
        }

        // See EnableModuleForSiteHandler's own identical guard for why this is checked ahead of the
        // per-site overlap loop below.
        var reservedConflict = command.TriggerWords.FirstOrDefault(ReservedChatCommands.IsReserved);
        if (reservedConflict is not null)
        {
            return ConversationErrors.ModuleTriggerWordReserved(reservedConflict);
        }

        var existingOnSite = await moduleReadStore.GetForSiteAsync(command.SiteId, now, cancellationToken);
        foreach (var existing in existingOnSite)
        {
            if (existing.ModuleKey == moduleKey)
            {
                continue;
            }

            var conflictingWord = existing.TriggerWords.FirstOrDefault(existingWord =>
                command.TriggerWords.Any(candidate => string.Equals(candidate, existingWord, StringComparison.OrdinalIgnoreCase)));
            if (conflictingWord is not null)
            {
                return ConversationErrors.ModuleTriggerWordAlreadyRegistered(conflictingWord, existing.ModuleKey.Value);
            }
        }

        EnabledModule module;
        try
        {
            module = new EnabledModule(
                new EnabledModuleId(idGenerator.NewId(now)), command.SiteId, moduleKey, command.TriggerWords,
                entryPoint, credential, now, grantedByOwner: true, expiresAt: command.ExpiresAt);
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

        // `23-102`/`adr/0151`: the entitlement this call is about to persist below is worthless without
        // the vocabulary to exercise it - see IModulePermissionsProvider's own remarks for what "the
        // permissions module K needs" means and where it comes from. Seeded before EnabledModule itself
        // is persisted, not after: IRoleRepository.AddPermissionsAsync is idempotent (safe to run on
        // every grant, including a repeat of one that already ran), so ordering it first means a failure
        // here never leaves an EnabledModule row granting an entitlement nobody can yet act on - exactly
        // the gap this item exists to close, one step earlier rather than one step later. Runs
        // unconditionally on every call, including a re-grant of a module this site already holds -
        // that repeat is exactly how the four sites this item's own backlog item names get fixed: no
        // separate backfill, the same write path a fresh grant already takes.
        var permissions = modulePermissions.Get(moduleKey);
        await roles.AddPermissionsAsync(command.SiteId, "Operator", permissions.OperatorPermissions, cancellationToken);
        await roles.AddPermissionsAsync(command.SiteId, "Admin", permissions.AdminPermissions, cancellationToken);

        await modules.SaveAsync(module, cancellationToken);

        // `23-59`/`adr/0147`: the second, independent fact - "this site's contacts collected before
        // today are now owed a retroactive carry-over" - recorded only once the grant itself is
        // durable, so a failure staging this request never leaves an EnabledModule row nobody actually
        // granted. Deliberately unconditional on ModuleKey, unlike IModulePermissionsProvider.Get just
        // above: adr/0065 decision 2 keeps this assembly ignorant of what a module even is, and a
        // literal `moduleKey.Value == "calendar"` branch here would be exactly the leak that decision
        // forbids (the same "chat must not know what a module is" reasoning IModulePermissionsProvider's
        // own remarks give for its own opaque-key lookup). The alternative considered - a second
        // deployment-configured provider, "does module K want a contact carry-over" - was rejected as
        // premature generalisation for a yes/no that today has exactly one real answer: unlike
        // permissions, where different modules genuinely need different sets, there is only one kind of
        // fact chat could ever carry over (a contact), so every grant requesting one costs a few no-op
        // outbox rows for a module nobody subscribes to and buys no real flexibility in return. Staging
        // this request is cheap (one upsert) regardless of which module was granted; the request itself
        // is inert until ContactCarryoverJob processes it.
        await contactCarryover.RequestAsync(command.SiteId, now, cancellationToken);

        return module.Id;
    }
}
