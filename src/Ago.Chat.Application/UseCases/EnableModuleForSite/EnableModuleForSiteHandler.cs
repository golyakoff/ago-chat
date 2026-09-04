using Ago.Chat.Application.Abstractions;
using Ago.Chat.Domain;
using Ago.Platform.Kernel;

namespace Ago.Chat.Application.UseCases.EnableModuleForSite;

/// <summary>
/// `20-07`: registers "site X has module K enabled" - the one write the whole seam depends on. The
/// trigger-word-conflict rule is enforced here, at registration time, rather than at routing time:
/// letting two enabled modules on one site register the identical trigger word would make
/// <see cref="TriggerCommandMatcher"/> silently first-match-wins on whichever module happens to sort
/// first, which is exactly the kind of "nobody decided this, the code did" behaviour this item's own
/// backlog item calls out by name ("a real test proving rejection, not first-match-wins").
///
/// <para><b>Checked against every other <em>enabled</em> module on the site, not just one.</b> A site
/// with three modules registering a fourth needs its new trigger words checked against all three
/// existing rows, not merely the most recently registered one - <see cref="IEnabledModuleReadStore"/>
/// already returns the whole site's enabled set for exactly this reason.</para>
///
/// <para><b>Re-registering the same <see cref="ModuleKey"/> is an update, not a conflict</b> - its own
/// previous trigger words are excluded from the overlap check (they cannot conflict with themselves),
/// and <see cref="IEnabledModuleRepository.SaveAsync"/> upserts by <see cref="EnabledModuleId"/>... but
/// this handler always mints a fresh one, so in practice a second registration for the same module
/// produces a second row rather than an update. That is a real, honestly-stated limitation: there is no
/// "one row per (site, module)" uniqueness enforced here today, because nothing in this item's own scope
/// needed re-registration - the registry has exactly one writer so far (a test, or a future internal
/// endpoint) and nothing has ever called it twice for the same module. Flagged rather than silently
/// left, per the backlog item's own instruction to say plainly where an implementer's-call default was
/// made.</para>
///
/// <para><b>`22-11`: the module-side registration is made real before this row is ever saved.</b> This
/// handler calls <see cref="IModuleRegistrationGateway.RegisterAsync"/> synchronously, in the same
/// request, before <see cref="IEnabledModuleRepository.SaveAsync"/> - not through the outbox. If the
/// module refuses or is unreachable, nothing is written on this side either: the operator sees the
/// failure immediately and the retry is simply calling this command again (both sides of the
/// underlying HTTP call are idempotent - a second `PUT .../module-registrations/{siteId}` for the
/// still-unregistered site succeeds the same way the first attempt would have). See this repository's
/// own report for the fuller argument against an eventually-consistent, outbox-dispatched
/// alternative.</para>
///
/// <para><b>`22-17`: this handler's own grant always carries <c>GrantedByOwner: false</c> and
/// <c>ExpiresAt: null</c>.</b> A tenant configuring their own module is not a trial - see
/// <see cref="EnabledModule.ExpiresAt"/>'s own remarks and <see cref="EnableModuleForSiteAsOwner.EnableModuleForSiteAsOwnerHandler"/>,
/// the sibling handler the platform owner's own grant goes through instead of this one.</para>
/// </summary>
public sealed class EnableModuleForSiteHandler(
    IEnabledModuleRepository modules, IEnabledModuleReadStore moduleReadStore, IPermissionChecker permissions,
    IModuleRegistrationGateway registrationGateway, ISiteRepository sites, IClock clock, IIdGenerator idGenerator)
{
    public async Task<Result<EnabledModuleId>> HandleAsync(
        EnableModuleForSite command, CancellationToken cancellationToken)
    {
        var allowed = await permissions.HasPermissionAsync(
            command.RequestedBy, command.SiteId, Permission.SiteConfigure, cancellationToken);
        if (!allowed)
        {
            return ConversationErrors.Forbidden("Operator does not have permission to configure this site's modules.");
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

        // `14-12`/`docs/conventions/text-commands.md`: refused once, here, rather than left to be
        // discovered as a runtime precedence question between two vocabularies - see
        // ReservedChatCommands' own remarks for why this codebase treats that as the wrong place to
        // resolve a collision at all. Checked ahead of the per-site overlap loop below: a reserved word
        // is refused regardless of whether any other module on this site happens to have registered it
        // too.
        var reservedConflict = command.TriggerWords.FirstOrDefault(ReservedChatCommands.IsReserved);
        if (reservedConflict is not null)
        {
            return ConversationErrors.ModuleTriggerWordReserved(reservedConflict);
        }

        var now = clock.UtcNow;
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
                entryPoint, credential, now, grantedByOwner: false, expiresAt: null);
        }
        catch (ArgumentException ex)
        {
            return ConversationErrors.ModuleInvalid(ex.Message);
        }

        // `22-17`: an opaque display name for the module's own provisioning call - see
        // IModuleRegistrationGateway.RegisterAsync's own remarks on why this is not "chat learning a
        // product's name". A site with no name recorded (Site.Name's own remarks: optional, ~60
        // legacy call sites) falls back to something a module's own admin surface can still show.
        var site = await sites.GetByIdAsync(command.SiteId, cancellationToken);
        var displayName = string.IsNullOrWhiteSpace(site?.Name) ? $"site-{command.SiteId.Value}" : site.Name;

        // `22-11`: the module deployment confirms the registration before this row is ever
        // persisted - see this handler's own remarks on the ordering.
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
