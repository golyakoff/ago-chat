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
/// </summary>
public sealed class EnableModuleForSiteAsOwnerHandler(
    IEnabledModuleRepository modules, IEnabledModuleReadStore moduleReadStore,
    IModuleRegistrationGateway registrationGateway, ISiteRepository sites, IClock clock, IIdGenerator idGenerator)
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
        Uri entryPoint;
        ModuleCredential credential;
        ModuleProvisioningSecret provisioningSecret;
        try
        {
            moduleKey = new ModuleKey(command.ModuleKey);
            entryPoint = new Uri(command.EntryPoint, UriKind.Absolute);
            credential = new ModuleCredential(command.Credential);
            provisioningSecret = new ModuleProvisioningSecret(command.ProvisioningSecret);
        }
        catch (Exception ex) when (ex is ArgumentException or UriFormatException)
        {
            return ConversationErrors.ModuleInvalid(ex.Message);
        }

        if (entryPoint.Scheme != Uri.UriSchemeHttp && entryPoint.Scheme != Uri.UriSchemeHttps)
        {
            return ConversationErrors.ModuleInvalid("A module entry point must be an absolute http(s) URL.");
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

        await modules.SaveAsync(module, cancellationToken);
        return module.Id;
    }
}
